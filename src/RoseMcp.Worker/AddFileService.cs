using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Creates a C# file: in the right project, with the right namespace, in the file conventions of
/// the repository around it, and with the imports its code needs.
/// <para>
/// The first thing most work does is start a class, so this is the earliest place a session can be
/// lost. Writing the file with a text tool leaves the workspace permanently mid-edit and a build
/// being paid for anyway, and from there none of the semantic reads is worth reaching for -- the
/// whole diagnosis, arriving at step one.
/// </para>
/// <para>
/// Five things a text write gets wrong and this does not. Which project claims the path, which
/// decides whether the file is in the build at all. The namespace, which the folder decides and
/// IDE0130 enforces. Tabs, braces, line endings and the final newline. The imports. And whether
/// the file is already there, which is a refusal rather than an overwrite.
/// </para>
/// </summary>
public static class AddFileService
{
	/// <summary>
	/// How many distinct unresolved names to look an import up for. Higher than a member edit's,
	/// because ten unresolved names in a new class is an ordinary new class rather than a sign the
	/// code is wrong.
	/// </summary>
	private const int Looked = 20;

	/// <summary>How many introduced errors come back before the caller should read the file instead.</summary>
	public static async Task<MutationResult<AddFileResult>> AddAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		AddFileRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var edit = EditPipeline.Begin(
			snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		var notices = edit.Notices;
		var path = Path.GetFullPath(request.FilePath);

		progress?.Report($"Placing {Path.GetFileName(path)}", 0);

		var listed = Refuse(snapshot.Solution, path);

		var project = listed.Length > 0
			? snapshot.Solution.GetDocument(listed[0])!.Project
			: Owner(snapshot.Solution, path, request.Project);

		// From the repository and the files around the new one, never from the code: composed for a tool
		// argument, that is LF whatever the repository uses.
		var rules = await Whitespace.RulesForNewAsync(project, path, from: null, cancellationToken);

		// Before anything is put around the code, so a literal is named by its line in what was sent.
		string? rewrote = null;
		var code = MemberSyntax.WithEndings(
			request.Code, rules.LineEnding, literals => rewrote = MemberEditService.RewrittenEndings(literals, rules));

		var unit = Parse(code, project.ParseOptions);
		var space = Namespace(unit, project, path, notices);

		progress?.Report("Writing the file", 25);

		var built = Build(unit, space);
		// Into the document a listing project already has for the path, where there is one: adding a
		// second would put the file in the compilation twice.
		var id = listed.Length > 0 ? listed[0] : DocumentId.CreateNewId(project.Id, Path.GetFileName(path));

		var added = listed.Length > 0
			? snapshot.Solution.WithDocumentText(id, SourceText.From(built))
			: snapshot.Solution.AddDocument(
				id, Path.GetFileName(path), SourceText.From(built), Folders(project, path), path);

		// Into the document once it is in its project, so what is already in scope is the compilation's
		// answer, and before the formatter, so one pass lays out the imports with everything else.
		var imported = await WithUsingsAsync(added, id, request.Usings, rules, notices, cancellationToken);

		var solution = await FormatAsync(imported, id, rules, cancellationToken);

		var imports = ResolvedImports.Imports.None;

		if (request.ResolveUsings)
		{
			progress?.Report("Working out which namespaces the code needs", 45);

			(solution, imports) = await WithImportsAsync(
				diagnostics, snapshot.Solution, solution, id, path, rules, cancellationToken);
		}

		// A multi-targeted project that lists the path has a document for it per target, and each gets
		// the text the first was given.
		foreach (var other in listed.Skip(1))
		{
			solution = solution.WithDocumentText(other, await solution.GetDocument(id)!.GetTextAsync(cancellationToken));
		}

		progress?.Report(request.Apply ? "Writing to disk" : "Building the diff", 70);

		// A file that is not there yet, so nothing already there was asked to change.
		await edit.WriteAsync(solution, Asked.Nothing, cancellationToken);

		if (request.Verify) progress?.Report("Compiling to see what the file did", 80);

		await edit.VerifyAsync(
			path, EditVerification.ScopeFor(solution, path, reaches: null, request.VerifyScope), [path], cancellationToken);

		// In the build when the project globs its directory or names the file itself: a project that
		// lists its files compiles the ones it lists, whether or not they were on disk when it loaded.
		var projectText = await ProjectTextAsync(project, cancellationToken);
		var inTheBuild = ProjectItemStyle.GlobsSourceFiles(projectText)
			|| listed.Length > 0
			|| (Path.GetDirectoryName(project.FilePath) is { } directory && ProjectItemStyle.Lists(projectText, directory, path));

		// Read off the solution the file was written from, so a literal is named against the line it
		// ends up on rather than the line the caller wrote it at.
		var literalEndings = await LiteralEndingsAsync(solution, id, rules, request.Code, cancellationToken);

		var importsAdded = ResolvedImports.Report(imports);

		notices.AddRange(Notices(request, imports, inTheBuild, project, rewrote, literalEndings));

		if (DefaultLayout(rules, Path.GetFileName(path)) is { } defaulted) notices.Add(defaulted);

		notices.AddRange(edit.Report());

		var result = new AddFileResult
		{
			Revision = snapshot.Revision,
			Project = project.Name,
			Namespace = space,
			Types = [.. TypeNames(unit)],
			Applied = request.Apply,
			InTheBuild = inTheBuild,
			ImportsAdded = importsAdded,
			ImportsAmbiguous = imports.Ambiguous,
			Unresolved = imports.Unresolved,
			Diff = edit.Outcome.Diff,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			PreexistingErrorCount = edit.Verification.PreexistingCount,
			ProjectsChecked = edit.Verification.Projects,
			ChangedFiles = edit.Outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<AddFileResult>(result, request.Apply ? solution : null);
	}

	/// <summary>
	/// Refuses a path that is already something, and returns the documents a project already lists for
	/// a path that is not on disk yet. Overwriting a file is not what "add" means, and a caller that meant
	/// to replace one has three tools that say so.
	/// <para>
	/// A project that lists its files loads a document for every <c>Compile</c> item it names, there or
	/// not, so naming the file in the project first and then creating it is the ordinary order of work
	/// there. A document with no file behind it is the place the new file goes, not a file that exists:
	/// refusing it as "already in the solution" leaves the caller no tool that can create the file at all.
	/// </para>
	/// </summary>
	private static ImmutableArray<DocumentId> Refuse(Solution solution, string path)
	{
		if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"{Path.GetFileName(path)} is not a .cs file, and this writes C#.");
		}

		var listed = solution.GetDocumentIdsWithFilePath(path);
		var exists = File.Exists(path);

		if (listed.Length > 0 && exists)
		{
			throw new ArgumentException(
				$"{Path.GetFileName(path)} is already in the solution. Write into it with rose_add_member, "
					+ "rose_replace_member or rose_replace_body.");
		}

		if (exists)
		{
			throw new ArgumentException(
				$"{path} already exists on disk. This creates a file rather than overwriting one.");
		}

		return listed;
	}

	/// <summary>
	/// The project that will compile the file, chosen by whose directory contains the path.
	/// <para>
	/// Counted by project file rather than by compilation. A multi-targeted project is several
	/// Roslyn projects over one set of files, so counting compilations would find three claimants
	/// for every file in it and refuse the ordinary case.
	/// </para>
	/// </summary>
	private static Project Owner(Solution solution, string path, string? named)
	{
		// The first of several only where they are one project file loaded per framework, which share
		// the file and compile it alike.
		if (named is { Length: > 0 }) return ProjectNames.Resolve(solution, named)[0];

		var containing = solution.Projects
			.Where(project => project.FilePath is { Length: > 0 } && Contains(Path.GetDirectoryName(project.FilePath)!, path))
			.ToArray();

		var files = containing
			.Select(project => project.FilePath!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (files.Length == 0)
		{
			throw new ArgumentException(
				$"{path} is not inside the directory of any project in {SolutionName(solution)}, so nothing here would "
					+ "compile it. Put it under one of its projects, name one with the project argument, or, where it "
					+ "belongs to another solution, name that solution with the workspace argument.");
		}

		if (files.Length > 1)
		{
			throw new ArgumentException(
				$"{path} is inside {files.Length} projects' directories: "
					+ $"{string.Join(", ", files.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal))}. "
					+ "Name the one that should compile it.");
		}

		// The deepest-nested project wins nothing here, because there is only one. Several
		// compilations of it are multi-targeting, and the first serves for placing a file.
		return containing[0];
	}

	/// <summary>
	/// The solution's file name, for a refusal that is only true of this solution: a path outside every
	/// project here may sit inside a project of another solution, and a sentence that says "any project"
	/// tells a caller who routed to the wrong one that the path itself is the mistake.
	/// </summary>
	private static string SolutionName(Solution solution) =>
		solution.FilePath is { Length: > 0 } file ? Path.GetFileName(file) : "this solution";

	private static bool Contains(string directory, string path) =>
		Path.GetFullPath(path).StartsWith(
			Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The code, parsed before anything is placed, so a refusal costs nothing.
	/// </summary>
	private static CompilationUnitSyntax Parse(string code, ParseOptions? options)
	{
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("No code was supplied, so there is nothing to write.");

		var unit = SyntaxFactory.ParseCompilationUnit(code, options: options as CSharpParseOptions);

		var errors = unit.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.Take(5)
			.ToArray();

		if (errors.Length > 0)
		{
			var described = string.Join("; ", errors.Select(error =>
				$"line {error.Location.GetLineSpan().StartLinePosition.Line + 1}: {error.GetMessage()}"));

			throw new ArgumentException($"The code does not parse, so nothing was written. {described}");
		}

		if (unit.Members.Count == 0) throw new ArgumentException("The code declares nothing, so there is no file to write.");

		return unit;
	}

	/// <summary>
	/// The namespace the file will declare: the one the code wrote, or the one its folder implies.
	/// <para>
	/// A namespace the code declares is honoured even where it disagrees with the folder, because
	/// there are real reasons to want that and refusing would make the tool unusable for them. The
	/// disagreement is said out loud, since IDE0130 is a build error in repositories that turn it
	/// up and the caller may not have meant it.
	/// </para>
	/// </summary>
	private static string Namespace(CompilationUnitSyntax unit, Project project, string path, List<string> notices)
	{
		// Top-level statements run in the global namespace and can follow no namespace declaration, so
		// a file of them is given none: wrapped in the folder's, its statements become members a
		// namespace cannot hold and every type beside them lands in the global namespace anyway -- so
		// the file does not compile and the namespace reported is not the one anything is declared in.
		if (HasTopLevelStatements(unit))
		{
			notices.Add("The code is top-level statements, so the file declares no namespace: they run in the "
				+ "global namespace, and so do the types declared beside them.");

			return string.Empty;
		}

		var derived = Derived(project, path);

		var declared = unit.Members
			.OfType<BaseNamespaceDeclarationSyntax>()
			.Select(declaration => declaration.Name.ToString())
			.FirstOrDefault();

		if (declared is null) return derived;

		if (!string.Equals(declared, derived, StringComparison.Ordinal))
		{
			notices.Add($"The code declares namespace {declared}, and the folder implies {derived}. The code's "
				+ "was kept. Where IDE0130 is turned up, a namespace that does not match its folder is a "
				+ "build error.");
		}

		return declared;
	}

	/// <summary>Whether the code is a program's top-level statements rather than declarations alone.</summary>
	private static bool HasTopLevelStatements(CompilationUnitSyntax unit) =>
		unit.Members.OfType<GlobalStatementSyntax>().Any();

	/// <summary>
	/// What the folder says the namespace should be: the project's own default, plus a segment for
	/// each directory between the project and the file.
	/// </summary>
	private static string Derived(Project project, string path)
	{
		var root = project.DefaultNamespace is { Length: > 0 } declared ? declared : project.Name;
		var folders = Folders(project, path);

		return folders.Count == 0 ? root : $"{root}.{string.Join(".", folders)}";
	}

	private static IReadOnlyList<string> Folders(Project project, string path)
	{
		if (project.FilePath is not { Length: > 0 } file) return [];

		var directory = Path.GetFullPath(Path.GetDirectoryName(file)!);
		var relative = Path.GetRelativePath(directory, Path.GetFullPath(Path.GetDirectoryName(path)!));

		return relative is "." or ".."
			? []
			: [.. relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				.Where(segment => segment.Length > 0 && segment != ".")];
	}

	/// <summary>
	/// The file's text: the namespace, then what the caller wrote. Built as text and parsed by the
	/// formatter afterwards rather than assembled as syntax, because the shape being produced is a file
	/// and every part of it is decided by the repository's own rules.
	/// <para>
	/// The usings argument is not written here, because a block prepended to the text never meets the
	/// imports the code declares: code opening with <c>using System.Reflection;</c> and an argument naming
	/// a namespace outside System make two blocks in the wrong order, which compiles and fails
	/// <c>dotnet format</c> on import ordering. <see cref="WithUsingsAsync"/> places each one among the
	/// file's own instead.
	/// </para>
	/// </summary>
	private static string Build(CompilationUnitSyntax unit, string space) =>
		// The caller's own text, never NormalizeWhitespace. That regenerates every piece of trivia in
		// the file from scratch, which loses in one operation the blank lines between using groups,
		// between members and inside a body, the wrapping of a chained call, and the spacing inside a
		// documentation tag -- none of which any rule here has an opinion about. What the repository
		// does enforce is applied afterwards by the formatter and the whitespace pass.
		unit.Members.OfType<BaseNamespaceDeclarationSyntax>().Any() || HasTopLevelStatements(unit)
			? unit.ToFullString()
			: WithNamespace(unit, space);

	/// <summary>
	/// The declarations under a file-scoped namespace, which is what this repository's convention
	/// and IDE0161 both ask for and what every modern SDK template writes.
	/// <para>
	/// Sliced out of the caller's own text rather than rebuilt from the parsed pieces. Re-joining
	/// the usings with one newline and the members with two produces a file that reads plausibly and
	/// has lost every blank line the caller put between using groups and inside a body -- structure
	/// somebody wrote on purpose, and which nothing downstream can put back because nothing
	/// downstream knows it was there. Only the join between the two halves is decided here, since
	/// that is the part being introduced.
	/// </para>
	/// </summary>
	private static string WithNamespace(CompilationUnitSyntax unit, string space)
	{
		var text = unit.ToFullString();
		var split = unit.Usings.Count == 0 ? 0 : unit.Usings[^1].FullSpan.End;

		var head = text[..split].TrimEnd();
		var body = text[split..].Trim();

		var imports = head.Length == 0 ? string.Empty : $"{head}\n\n";

		return body.Length == 0 ? $"{imports}namespace {space};\n" : $"{imports}namespace {space};\n\n{body}\n";
	}

	/// <summary>
	/// The new document with the usings argument imported into it, each where the file's own imports
	/// put it -- the placement <c>rose_add_using</c> gives an existing file, since the code a caller
	/// sends often declares imports of its own and the two have to end up one ordered list.
	/// <para>
	/// Asked of the compilation, before the file is formatted so one pass lays out both: an import an
	/// implicit or global using already covers, or the namespace the file is in, is not written, because
	/// writing it is IDE0005. Each of those is said, in the words the member tools use, since the caller
	/// named it and will otherwise look for it in the file. An alias whose name already stands for
	/// something else is refused here, before anything is written.
	/// </para>
	/// </summary>
	private static async Task<Solution> WithUsingsAsync(
		Solution solution,
		DocumentId id,
		IReadOnlyList<string> usings,
		WhitespaceRules rules,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		if (usings.Count == 0) return solution;

		var document = solution.GetDocument(id)
			?? throw new InvalidOperationException("The document being written left the solution mid-edit.");

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);

		if (model is null || tree is null || await tree.GetRootAsync(cancellationToken) is not CompilationUnitSyntax root)
		{
			throw new InvalidOperationException($"{document.Name} is not a C# source file.");
		}

		var style = UsingStyle.For(document.Project, tree, root, rules.LineEnding);
		var insertion = UsingDirectives.Ensure(root, model, usings, style, cancellationToken);

		foreach (var covered in insertion.AlreadyInScope)
		{
			notices.Add($"Did not import {covered}.");
		}

		return insertion.Changed ? solution.WithDocumentSyntaxRoot(id, insertion.Root) : solution;
	}

	/// <summary>The two formatting passes, over the whole file, since the whole file is new.</summary>
	private static async Task<Solution> FormatAsync(Solution solution, DocumentId id, WhitespaceRules rules, CancellationToken cancellationToken)
	{
		var document = solution.GetDocument(id)
			?? throw new InvalidOperationException("The document being written left the solution mid-edit.");

		var options = await Whitespace.FormattingOptionsAsync(document, rules, cancellationToken);
		var formatted = await Formatter.FormatAsync(document, options, cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		return formatted.WithText(Whitespace.Apply(root, text, rules)).Project.Solution;
	}

	/// <summary>
	/// Compiles the new file, works out what would import the names that did not bind, and adds the
	/// ones with a single answer. Costs a compilation the verification would have paid for anyway.
	/// </summary>
	private static async Task<(Solution Solution, ResolvedImports.Imports Imports)> WithImportsAsync(
		DiagnosticsService diagnostics,
		Solution before,
		Solution after,
		DocumentId id,
		string path,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		var project = after.GetDocument(id)?.Project.Name;
		if (project is null) return (after, ResolvedImports.Imports.None);

		var verification = await EditVerification.RunAsync(
			diagnostics, before, after, [project], path, [], cancellationToken);

		var imports = await ResolvedImports.ForAsync(
			new WorkspaceSnapshot { Solution = after, Revision = 0 },
			verification.Introduced,
			path,
			Looked,
			cancellationToken);

		return await ResolvedImports.ApplyResolvingAsync(after, id, imports, rules, cancellationToken);
	}

	/// <summary>
	/// What to say about a multi-line literal in the new file whose endings are not the file's, or
	/// null where it holds none.
	/// <para>
	/// Code whose every ending is a bare LF has had them all rewritten to the file's before this,
	/// literals included, so what is left here is code that carried a CR: a caller thinking about
	/// endings, whose literals are kept exactly as they arrived because an ending inside one is part of
	/// the string's value. A whole file of literals arrives at once, so this is where the most of them
	/// collect -- sixty-four ENDOFLINE errors on one added file, every one inside a raw literal -- and
	/// it is <c>rose_format</c>'s own sentence, so a caller who runs both is not told two different
	/// things. What is added to it is the way this tool offers to have them rewritten.
	/// </para>
	/// </summary>
	private static async Task<string?> LiteralEndingsAsync(
		Solution solution,
		DocumentId id,
		WhitespaceRules rules,
		string sent,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(id) is not { } document) return null;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);

		if (root is null || tree is null) return null;

		var text = await document.GetTextAsync(cancellationToken);

		var notice = Whitespace.LiteralEndingNotice(root, text, rules, document.Name);
		if (notice is null) return null;

		var carriedCr = sent.Contains('\r', StringComparison.Ordinal);

		return carriedCr
			? notice + " The code carried a CR, so every ending in it was kept as it arrived; sent with bare LFs "
				+ "only, its literals take the file's endings with everything else."
			: notice;
	}

	/// <summary>
	/// What to say when nothing decided part of the new file's layout -- no .editorconfig reaching it, no
	/// eol in the repository's attributes, no file beside it to follow -- so Roslyn's defaults did, or null
	/// where something decided all of it. The defaults are a guess about a repository that said nothing,
	/// and the cheapest moment to correct one is before the files that follow take the first one's lead.
	/// </summary>
	private static string? DefaultLayout(WhitespaceRules rules, string name)
	{
		var indent = rules.IndentFrom == LayoutSource.Default;
		var ending = rules.LineEndingFrom == LayoutSource.Default;

		if (!indent && !ending) return null;

		var what = (indent, ending) switch
		{
			(true, true) => $"four spaces and {LineEndings.Name(rules.LineEnding)}",
			(true, false) => "four spaces",
			_ => LineEndings.Name(rules.LineEnding),
		};

		return $"{name} was written with {what}, which are Roslyn's defaults: nothing declares how files here are laid "
			+ "out and no file beside it shows a way. An .editorconfig saying so settles it for this file and every one after.";
	}

	private static async Task<string> ProjectTextAsync(Project project, CancellationToken cancellationToken)
	{
		if (project.FilePath is not { Length: > 0 } file || !File.Exists(file)) return string.Empty;

		return await File.ReadAllTextAsync(file, cancellationToken);
	}

	private static IEnumerable<string> TypeNames(CompilationUnitSyntax unit) =>
		unit.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>()
			.Select(type => type.Identifier.Text);

	/// <summary>
	/// What adding a file has to say that no other writing tool does. Everything about the write and
	/// the compile comes from <see cref="EditPipeline.Report"/>, which runs after this.
	/// </summary>
	private static IEnumerable<string> Notices(
		AddFileRequest request,
		ResolvedImports.Imports imports,
		bool inTheBuild,
		Project project,
		string? rewrote,
		string? literalEndings)
	{
		// Beside the other things the diff cannot show, and before the compile: an ending rewritten inside a
		// literal is a changed value no diff shows, and one left there is not a compile error and reads as
		// one only at the next dotnet format.
		if (rewrote is { } rewritten) yield return rewritten;
		if (literalEndings is { } endings) yield return endings;

		if (!inTheBuild)
		{
			yield return $"{project.Name} lists the files it compiles rather than globbing them, so this file is "
				+ "not in the build until the project names it. Nothing here can see it until then, and a "
				+ "compilation that does not include it reports no problems with it at all.";
		}

		foreach (var line in imports.Ambiguous) yield return line;
		foreach (var line in imports.Unresolved) yield return line;
	}
}
