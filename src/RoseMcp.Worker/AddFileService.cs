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
	private const int Listed = 20;

	public static async Task<MutationResult<AddFileResult>> AddAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		AddFileRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		var notices = new List<string>(snapshot.Notices);
		var path = Path.GetFullPath(request.FilePath);

		progress?.Report($"Placing {Path.GetFileName(path)}", 0);

		Refuse(snapshot.Solution, path);

		var project = Owner(snapshot.Solution, path, request.Project);
		var unit = Parse(request.Code, project.ParseOptions);
		var space = Namespace(unit, project, path, notices);

		progress?.Report("Writing the file", 25);

		var built = Build(unit, space, request.Usings, project);
		var id = DocumentId.CreateNewId(project.Id, Path.GetFileName(path));

		var added = snapshot.Solution.AddDocument(
			id, Path.GetFileName(path), SourceText.From(built), Folders(project, path), path);

		var solution = await FormatAsync(added, id, cancellationToken);

		var imports = ResolvedImports.Imports.None;

		if (request.ResolveUsings)
		{
			progress?.Report("Working out which namespaces the code needs", 45);

			(solution, imports) = await WithImportsAsync(
				diagnostics, snapshot.Solution, solution, id, path, cancellationToken);
		}

		progress?.Report(request.Apply ? "Writing to disk" : "Building the diff", 70);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, solution, request.Apply, noteSelfWrite, cancellationToken);

		var verification = Verification.NotRun;

		if (request.Verify)
		{
			progress?.Report("Compiling to see what the file did", 80);

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				solution,
				EditVerification.ScopeFor(solution, path, reaches: null, request.VerifyScope),
				path,
				cancellationToken);
		}

		var globs = ProjectItemStyle.GlobsSourceFiles(await ProjectTextAsync(project, cancellationToken));

		// Read off the solution the file was written from, so a literal is named against the line it
		// ends up on rather than the line the caller wrote it at.
		var literalEndings = await LiteralEndingsAsync(solution, id, cancellationToken);

		// After the verification, which is the first moment it can be said whether each import resolved
		// the error it was fetched for rather than only which namespace it named.
		var importsAdded = await ResolvedImports.ReportAsync(
			solution, imports, verification.Introduced, path, cancellationToken);

		notices.AddRange(Notices(request, verification, outcome, imports, globs, project, literalEndings));

		var result = new AddFileResult
		{
			Revision = snapshot.Revision,
			FilePath = path,
			Project = project.Name,
			Namespace = space,
			Types = [.. TypeNames(unit)],
			Applied = request.Apply,
			InTheBuild = globs,
			ImportsAdded = importsAdded,
			ImportsAmbiguous = imports.Ambiguous,
			Unresolved = imports.Unresolved,
			Diff = outcome.Diff,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ProjectsChecked = verification.Projects,
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<AddFileResult>(result, request.Apply ? solution : null);
	}

	/// <summary>
	/// Refuses a path that is already something. Overwriting a file is not what "add" means, and a
	/// caller that meant to replace one has three tools that say so.
	/// </summary>
	private static void Refuse(Solution solution, string path)
	{
		if (solution.GetDocumentIdsWithFilePath(path).Length > 0)
		{
			throw new ArgumentException(
				$"{Path.GetFileName(path)} is already in the solution. Write into it with rose_add_member, "
					+ "rose_replace_member or rose_replace_body.");
		}

		if (File.Exists(path))
		{
			throw new ArgumentException(
				$"{path} already exists on disk. This creates a file rather than overwriting one.");
		}

		if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"{Path.GetFileName(path)} is not a .cs file, and this writes C#.");
		}
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
		if (named is { Length: > 0 })
		{
			return solution.Projects.FirstOrDefault(project =>
					string.Equals(project.Name, named, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException(
					$"No project called '{named}'. The solution has "
						+ $"{string.Join(", ", solution.Projects.Select(project => project.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.");
		}

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
				$"{path} is not inside any project's directory, so nothing would compile it. Put it under a "
					+ "project, or name one with the project argument.");
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
	/// The file's text: the imports, then the namespace, then what the caller wrote. Built as text
	/// and parsed by the formatter afterwards rather than assembled as syntax, because the shape
	/// being produced is a file and every part of it is decided by the repository's own rules.
	/// <para>
	/// The imports are ordered by <see cref="UsingDirectives.Sorts"/>, the same comparison that
	/// places one among a file's existing imports. Sorting them here ordinally instead put anything
	/// alphabetically before "System" above it, which compiles and trips no analyzer -- two orderings
	/// were two chances to disagree, and they did. System-first is the language tooling's default and
	/// cannot be read from .editorconfig at this point, since the document it would be read for does
	/// not exist until this text does.
	/// </para>
	/// </summary>
	private static string Build(
		CompilationUnitSyntax unit,
		string space,
		IReadOnlyList<string> usings,
		Project project)
	{
		// The caller's own text, never NormalizeWhitespace. That regenerates every piece of trivia in
		// the file from scratch, which loses in one operation the blank lines between using groups,
		// between members and inside a body, the wrapping of a chained call, and the spacing inside a
		// documentation tag -- none of which any rule here has an opinion about. What the repository
		// does enforce is applied afterwards by the formatter and the whitespace pass.
		var body = unit.Members.OfType<BaseNamespaceDeclarationSyntax>().Any()
			? unit.ToFullString()
			: WithNamespace(unit, space);

		var order = Comparer<string>.Create((left, right) => UsingDirectives.Sorts(left, right, systemFirst: true));

		var imports = usings
			.Select(name => name.Trim().TrimEnd(';'))
			.Select(name => name.StartsWith("using ", StringComparison.Ordinal) ? name["using ".Length..] : name)
			.Where(name => name.Length > 0)
			.Distinct(StringComparer.Ordinal)
			.Order(order)
			.Select(name => $"using {name};")
			.ToArray();

		_ = project;

		return imports.Length == 0 ? body : $"{string.Join("\n", imports)}\n\n{body}";
	}

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

	/// <summary>The two formatting passes, over the whole file, since the whole file is new.</summary>
	private static async Task<Solution> FormatAsync(Solution solution, DocumentId id, CancellationToken cancellationToken)
	{
		var document = solution.GetDocument(id)
			?? throw new InvalidOperationException("The document being written left the solution mid-edit.");

		var formatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		var rules = Whitespace.RulesFor(formatted.Project, tree, text);

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
		CancellationToken cancellationToken)
	{
		var project = after.GetDocument(id)?.Project.Name;
		if (project is null) return (after, ResolvedImports.Imports.None);

		var verification = await EditVerification.RunAsync(
			diagnostics, before, after, [project], path, cancellationToken);

		var imports = await ResolvedImports.ForAsync(
			new WorkspaceSnapshot { Solution = after, Revision = 0 },
			verification.Introduced,
			path,
			Looked,
			cancellationToken);

		if (!imports.AnythingToAdd) return (after, imports);

		var added = await ResolvedImports.ApplyAsync(after, id, imports.Namespaces, cancellationToken);

		return (added, imports);
	}

	/// <summary>
	/// What to say about a multi-line literal in the new file whose endings are not the file's, or
	/// null where it holds none.
	/// <para>
	/// A whole file of literals arrives here at once, which is what makes this the tool that needs it
	/// most: sixty-four ENDOFLINE errors on one added file, every one inside a raw literal, and
	/// nothing in the result to say a single ending had been left as it arrived. Keeping them is the
	/// invariant -- an ending inside a literal is part of the string's value -- so the answer is the
	/// sentence rather than a rewrite, and it is <c>rose_format</c>'s own sentence so a caller who
	/// runs both is not told two different things.
	/// </para>
	/// </summary>
	private static async Task<string?> LiteralEndingsAsync(
		Solution solution,
		DocumentId id,
		CancellationToken cancellationToken)
	{
		if (solution.GetDocument(id) is not { } document) return null;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);

		if (root is null || tree is null) return null;

		var text = await document.GetTextAsync(cancellationToken);

		return Whitespace.LiteralEndingNotice(root, text, Whitespace.RulesFor(document.Project, tree, text), document.Name);
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

	private static IEnumerable<string> Notices(
		AddFileRequest request,
		Verification verification,
		WriteOutcome outcome,
		ResolvedImports.Imports imports,
		bool globs,
		Project project,
		string? literalEndings)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";

		foreach (var notice in outcome.Notices) yield return notice;

		// Beside the other things the diff cannot show, and before the compile: an ending left inside a
		// literal is not a compile error and reads as one only at the next dotnet format.
		if (literalEndings is { } endings) yield return endings;

		if (!globs)
		{
			yield return $"{project.Name} lists the files it compiles rather than globbing them, so this file is "
				+ "not in the build until the project names it. Nothing here can see it until then, and a "
				+ "compilation that does not include it reports no problems with it at all.";
		}

		foreach (var line in imports.Ambiguous) yield return line;
		foreach (var line in imports.Unresolved) yield return line;

		if (!verification.Ran) yield break;

		foreach (var notice in verification.Notices) yield return notice;

		var compiled = string.Join(", ", verification.Projects);

		yield return verification.Introduced.Count == 0
			? $"{compiled} compiles clean."
			: $"{verification.Introduced.Count} error(s) introduced in {compiled}.";
	}
}
