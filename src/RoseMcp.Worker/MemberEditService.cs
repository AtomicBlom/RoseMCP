using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Writes C# by symbol: over a whole member, over a body, or into a type.
/// <para>
/// This is the first thing here that puts new code somewhere. Renaming, moving a type, applying a
/// fix and formatting all adjust code that already exists, so every actual change an agent made went
/// through text tools -- and in one long session on this repository, fourteen distinct mechanical
/// failures came out of that, not one of which was a wrong decision about what to change. They were
/// all in applying a change already decided: a heredoc that ate a backslash, a splice that dropped a
/// <c>private</c> and duplicated a brace, escapes that leaked into source, stripped line endings that
/// failed the build on IDE0055.
/// </para>
/// <para>
/// Every one of those is a category the compiler cannot produce. It parses, so it cannot emit an
/// unbalanced brace; it knows a member's span, so it cannot drop the modifier above it; it writes
/// through the formatter, so it cannot get tabs or line endings wrong. The order matters as much as
/// the mechanism: the code is parsed and the declaration resolved <em>before</em> the file is
/// touched, so a refusal costs nothing and leaves nothing half-written.
/// </para>
/// <para>
/// And it compiles afterwards and says what changed, because the reason the semantic reads went
/// unused was that a text edit path left the workspace permanently mid-edit and a build was being
/// paid for anyway. Answering "did that work" in the same call is what removes the build from the
/// loop.
/// </para>
/// </summary>
public static class MemberEditService
{
	/// <summary>
	/// How many introduced errors come back. Past twenty the caller has broken something structural
	/// and needs to look at the edit rather than at the list.
	/// </summary>
	private const int Listed = 20;

	/// <summary>
	/// Errors that mean a name did not resolve, which is usually an import rather than a mistake.
	/// <para>
	/// Wider than the set <see cref="MissingImports"/> looks a namespace up for, and deliberately so:
	/// CS0234 says a qualified name's left-hand side resolved and its right-hand side did not, which
	/// no import fixes, but it still belongs in the advice about what an unresolved name means.
	/// </para>
	/// </summary>
	private static readonly string[] Unresolved = ["CS0246", "CS0103", "CS0234"];

	/// <summary>
	/// How many distinct unresolved names an import is looked up for. An edit that introduces forty
	/// has gone wrong in a way no import list will fix, and forty searches would make reporting that
	/// failure slower than the failure.
	/// </summary>
	private const int Looked = 5;

	public static async Task<MutationResult<MemberEditResult>> EditAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		MemberEditRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Locating {request.Symbol}", 0);

		var written = request.Kind switch
		{
			MemberEditKind.Add => await AddAsync(snapshot.Solution, request, notices, cancellationToken),
			MemberEditKind.Delete => await DeleteAsync(snapshot.Solution, request, cancellationToken),
			MemberEditKind.ReplaceBody => await ReplaceBodyAsync(snapshot.Solution, request, notices, cancellationToken),
			_ => await ReplaceAsync(snapshot.Solution, request, notices, cancellationToken),
		};

		progress?.Report("Formatting what was written", 40);

		var imported = await WithImportsAsync(written, request, notices, cancellationToken);
		var finished = await FinishAsync(imported, cancellationToken);

		progress?.Report(request.Apply ? "Writing the file" : "Building the diff", 55);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, finished.Solution, request.Apply, noteSelfWrite, cancellationToken);

		if (outcome.ChangedFiles.Count == 0) notices.Add("The file already said exactly that, so nothing changed.");

		var verification = Verification.NotRun;
		var solution = finished.Solution;
		var path = written.Document.FilePath!;

		// A preview is verified too: what an edit would break is the question a preview is asking.
		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling to see what the edit did", 70);

			var scope = EditVerification.ScopeFor(solution, path, written.Reaches, request.VerifyScope);

			verification = await EditVerification.RunAsync(
				diagnostics, snapshot.Solution, solution, scope, path, cancellationToken);

			// Only where something did not bind, so an edit whose imports were right or unneeded pays
			// nothing for this and the one that needed it pays the compile it would have paid at the
			// next build.
			var wanted = request.ResolveUsings && verification.Introduced.Any(entry => MissingImports.IsUnresolved(entry.Id));

			if (wanted)
			{
				progress?.Report("Working out which namespaces the code needs", 80);

				solution = await ResolveImportsAsync(
					snapshot, solution, written, path, verification.Introduced, notices, cancellationToken);

				if (!ReferenceEquals(solution, finished.Solution))
				{
					outcome = await SolutionWriter.ApplyAsync(
						snapshot.Solution, solution, request.Apply, noteSelfWrite, cancellationToken);

					verification = await EditVerification.RunAsync(
						diagnostics, snapshot.Solution, solution, scope, path, cancellationToken);
				}
			}
		}

		notices.AddRange(finished.Notices);
		notices.AddRange(Notices(request, verification, outcome));

		var result = new MemberEditResult
		{
			Revision = snapshot.Revision,
			Symbol = written.Symbol,
			FilePath = written.Document.FilePath!,
			Line = finished.Line,
			Members = written.Members,
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ResolvedDiagnosticCount = verification.ResolvedCount,
			TotalErrorCount = verification.TotalCount,
			ProjectsChecked = verification.Projects,
			DependentsNotChecked = request.Verify && outcome.ChangedFiles.Count > 0
				? EditVerification.SkippedDependents(
					finished.Solution, written.Document.FilePath!, written.Reaches, request.VerifyScope)
				: [],
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? solution : null;

		return new MutationResult<MemberEditResult>(result, changed);
	}

	private static async Task<Written> ReplaceAsync(
		Solution solution,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindMemberAsync(solution, request.Symbol, request.FilePath, cancellationToken);

		GuardSharedDeclaration(target);

		var text = await target.Document.GetTextAsync(cancellationToken);

		var parsed = MemberSyntax.Parse(
			request.Code,
			KeywordAround(target.Declaration),
			target.Document.Project.ParseOptions,
			IndentAt(text, target.Declaration.SpanStart),
			Whitespace.Dominant(text),
			count => notices.Add(RewrittenEndings(count, text)),
			count => notices.Add(MemberSyntax.ReindentedLiteral(count)));

		if (parsed.Count != 1)
		{
			throw new ArgumentException(
				$"The code declares {parsed.Count} members and this replaces one. Add the rest with rose_add_member.");
		}

		var marker = new SyntaxAnnotation();
		var replacement = Positioned(parsed[0], target.Declaration, notices).WithAdditionalAnnotations(marker);
		var root = await RootOf(target.Document, cancellationToken);

		return new Written(
			target.Document,
			root.ReplaceNode(target.Declaration, replacement),
			marker,
			target.Signature,
			[.. NamesOf(parsed[0])],
			target.Symbol);
	}

	/// <summary>
	/// Takes a member out, with its documentation comment and its attributes, leaving the blank lines
	/// around it as one.
	/// <para>
	/// The only write that is safe semantically and nothing else: what makes a deletion wrong is
	/// invisible to a text edit. It is referenced somewhere, which the compile afterwards answers. It
	/// is one of several partial declarations, or an override whose base is abstract, so removing it
	/// breaks somewhere else entirely. Its documentation comment goes with it, or the next member
	/// inherits a summary describing something that is gone.
	/// </para>
	/// <para>
	/// The directives are the part a splice cannot get right. A member whose leading trivia opens a
	/// region and whose trailing trivia closes it leaves the file with CS1024 or CS1028 if the pair is
	/// cut in half, so removal keeps whatever is unbalanced and lets the region close around nothing.
	/// </para>
	/// </summary>
	private static async Task<Written> DeleteAsync(
		Solution solution,
		MemberEditRequest request,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindMemberAsync(solution, request.Symbol, request.FilePath, cancellationToken);

		GuardSharedDeclaration(target);

		if (target.Declaration.Parent is not { } parent)
		{
			throw new ArgumentException(
				$"{target.Signature} is not inside anything, so there is nothing to remove it from.");
		}

		var root = await RootOf(target.Document, cancellationToken);

		var without = parent.RemoveNode(
			target.Declaration,
			SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)
			?? throw new InvalidOperationException($"Removing {target.Signature} left nothing to write.");

		// Annotating the container rather than the member, because the member is what has gone. It is
		// what the formatting passes are pointed at, so they stay off the rest of the file.
		var marker = new SyntaxAnnotation();

		return new Written(
			target.Document,
			root.ReplaceNode(parent, without.WithAdditionalAnnotations(marker)),
			marker,
			target.Signature,
			[NameOfDeclaration(target.Declaration)],
			target.Symbol);
	}

	/// <summary>The name a removed declaration went by, for reporting what was taken out.</summary>
	private static string NameOfDeclaration(MemberDeclarationSyntax declaration) =>
		NamesOf(declaration).FirstOrDefault() ?? declaration.Kind().ToString();

	/// <summary>
	/// Replaces a body by rebuilding the member from its own signature text and the supplied body,
	/// then parsing that.
	/// <para>
	/// Rebuilding rather than grafting a parsed block onto the existing node is what makes the
	/// promise literal: the signature that comes out is the one that was there, character for
	/// character, because it was copied rather than regenerated. It also means a caller can switch a
	/// member between a block and an expression body without saying so, since both spellings parse
	/// against the same signature.
	/// </para>
	/// </summary>
	private static async Task<Written> ReplaceBodyAsync(
		Solution solution,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindMemberAsync(solution, request.Symbol, request.FilePath, cancellationToken);
		var declaration = target.Declaration;

		if (BodyStart(declaration) is not { } bodyStart)
		{
			throw new ArgumentException(
				$"{target.Signature} has no body to replace{WhyNoBody(declaration)}. rose_replace_member writes the "
					+ "whole declaration, which is how a member with more than one body is changed.");
		}

		var text = await target.Document.GetTextAsync(cancellationToken);
		var indent = IndentAt(text, declaration.SpanStart);

		// Joined by a single space, with whatever separated the old signature from its old body
		// dropped. The two are not interchangeable: a block body sat on the next line, so keeping
		// that break would leave an expression body's => stranded on a line of its own, which no
		// formatting rule then pulls back. One space lets .editorconfig decide where the brace of a
		// block goes, which is the only place that decision belongs.
		//
		// Prefixed with the indentation the file gives the first line, which the span begins after.
		// Without it a hand-wrapped parameter list reads as written at column zero, and the shift that
		// puts the member where it goes moves every continuation a level too deep -- and Roslyn's
		// formatter has no rule about where a wrapped list sits, so nothing downstream puts it back.
		// The signature then drifts on a change that promised to touch only the body.
		var head = indent + text.ToString(TextSpan.FromBounds(declaration.SpanStart, bodyStart)).TrimEnd();
		var written = BodyFor(declaration, target.Signature, text, bodyStart, request, notices);

		// The head is named as copied, which exempts it from the re-indentation the body needs. The
		// two halves arrive in different coordinate systems -- the signature indented for the file it
		// came out of, the body at whatever baseline the caller happened to write it at -- and one
		// baseline read off both takes a level from the lines the caller wrapped by hand and none
		// from the statements they belong to, landing a wrapped call flat against its own statement.
		// It is the same trap as the line above, arriving from the other side, and nothing catches
		// it: a continuation line is not a statement, so the formatter has no rule that puts it back.
		// An initialiser goes back as an expression and a semicolon; a body is wrapped in braces or
		// left behind its arrow. Sharing the rebuild is what keeps the copied-signature promise on
		// both: what comes out in front of the "=" is the text that was in front of it.
		var rebuilt = IsInitialiser(declaration)
			? $"{head} {written.Trim()};"
			: $"{head} {Body(written)}";

		// A body assembled from the file's own text is already indented for where it sits, and the
		// only caller code in it has been placed against the line it lands on -- so the destination's
		// own indentation is what comes off and goes back on, and the pass is the identity it should
		// be. Read from the code instead, the baseline is how deep the body sits inside its member,
		// and every line comes out a level shallower: invisibly for a block body, which the formatter
		// has rules for, and on disk for an expression body, which is a continuation it has none for.
		var fromTheFile = request.Find is { Length: > 0 } || request.Position is not null;

		// A body the caller supplied whole is measured against itself, as everything else here is, and
		// its lines belong one level in from the member: what precedes them is a brace or an arrow on
		// the signature's own line, not a line of their own to take a level from. Roslyn's formatter
		// puts a block's braces back where .editorconfig wants them and has no rule for an arrow.
		var rules = Whitespace.RulesFor(target.Document.Project, declaration.SyntaxTree, text);

		var parsed = MemberSyntax.Parse(
			rebuilt,
			KeywordAround(declaration),
			target.Document.Project.ParseOptions,
			fromTheFile ? indent : indent + rules.IndentUnit,
			Whitespace.Dominant(text),
			count => notices.Add(RewrittenEndings(count, text)),
			count => notices.Add(MemberSyntax.ReindentedLiteral(count)),
			copied: head,
			baseline: fromTheFile ? indent : null);

		if (parsed.Count != 1)
		{
			throw new ArgumentException(
				"The code closes the member early, so what follows it would become a second declaration.");
		}

		var marker = new SyntaxAnnotation();

		// The whole leading trivia is kept: the signature was not touched, so neither was anything
		// written about it.
		var replacement = parsed[0]
			.WithLeadingTrivia(declaration.GetLeadingTrivia())
			.WithAdditionalAnnotations(marker);

		var root = await RootOf(target.Document, cancellationToken);

		return new Written(
			target.Document,
			root.ReplaceNode(declaration, replacement),
			marker,
			target.Signature,
			[.. NamesOf(declaration)],
			Reaches: null);
	}

	/// <summary>
	/// The body to write, from whichever of the three payloads the caller sent.
	/// <para>
	/// Three because re-emitting a sixty-line body to change one line is what sends a caller back to a
	/// text anchor: the granularity is right and the payload is expensive. All three end here, as a
	/// whole body, so what reaches disk has been parsed and formatted either way.
	/// </para>
	/// </summary>
	private static string BodyFor(
		MemberDeclarationSyntax declaration,
		string signature,
		SourceText text,
		int bodyStart,
		MemberEditRequest request,
		List<string> notices)
	{
		var payloads = (request.Code.Length > 0 ? 1 : 0)
			+ (request.Find is { Length: > 0 } ? 1 : 0)
			+ (request.Position is not null ? 1 : 0);

		if (request.Position is not null && request.Code.Length > 0) payloads--;

		if (payloads == 0)
		{
			throw new ArgumentException(
				"Nothing to write. Pass code with the whole body, find and replace to change part of it, or "
					+ "position with code to insert at one end.");
		}

		if (payloads > 1)
		{
			throw new ArgumentException(
				"Pass one of code, find and replace, or position with code -- they are three ways of saying what "
					+ "the body becomes, and more than one leaves it ambiguous.");
		}

		if (request.Find is { Length: > 0 } find)
		{
			var body = text.ToString(TextSpan.FromBounds(bodyStart, declaration.Span.End)).TrimEnd(';', ' ', '\t');

			return BodyEdit.Anchored(
				body,
				find,
				request.Replace ?? string.Empty,
				request.IncludeTrivia,
				count => notices.Add(RewrittenEndings(count, text)));
		}

		if (request.Position is not { } position) return request.Code;

		if (BodyBlock(declaration) is not { } block) throw BodyEdit.NoStatements(signature);

		return BodyEdit.Inserted(declaration, block, request.Code, position == BodyPosition.Start, notices);
	}

	/// <summary>The block a member is written with, or null where it has an expression body instead.</summary>
	private static BlockSyntax? BodyBlock(MemberDeclarationSyntax declaration) =>
		declaration is BaseMethodDeclarationSyntax method ? method.Body : null;

	private static async Task<Written> AddAsync(
		Solution solution,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		if (request.After is { Length: > 0 } && request.Before is { Length: > 0 })
		{
			throw new ArgumentException("Pass after or before, not both: a member cannot go in two places.");
		}

		var target = await DeclarationLocator.FindTypeAsync(solution, request.Symbol, request.FilePath, cancellationToken);

		// An enum's members are items in a comma-separated list, and the comma belongs to neither
		// the item before it nor the one after. There is no member to write, so this declines rather
		// than writing something that would have to guess where the separators go.
		if (target.Declaration is not TypeDeclarationSyntax type)
		{
			throw new ArgumentException(
				$"{target.Signature} is an enum, whose members are items in a list rather than declarations, so one "
					+ "cannot be written as a member. rose_replace_member can still replace one that exists.");
		}

		var document = target.Document;

		var text = await document.GetTextAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken)
			?? throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");

		var rules = Whitespace.RulesFor(document.Project, tree, text);
		var lineEnding = rules.LineEnding;

		var parsed = MemberSyntax.Parse(
			request.Code,
			MemberSyntax.KeywordOf(type),
			document.Project.ParseOptions,
			IndentFor(type, text, rules),
			lineEnding,
			count => notices.Add(RewrittenEndings(count, text)),
			count => notices.Add(MemberSyntax.ReindentedLiteral(count)));

		GuardDuplicates(type, parsed);

		var index = PlacementIndex(type, request);

		var marker = new SyntaxAnnotation();
		var prepared = new List<MemberDeclarationSyntax>(parsed.Count);

		// The member being pushed down usually carries the blank line above it as its own leading
		// trivia, in which case it is already separated and adding another gives two.
		var followerIsSeparated = index >= type.Members.Count || StartsBlank(type.Members[index]);

		for (var position = 0; position < parsed.Count; position++)
		{
			prepared.Add(MemberSyntax.Prepared(
				parsed[position],
				blankBefore: position > 0 || index > 0,
				blankAfter: position == parsed.Count - 1 && !followerIsSeparated,
				lineEnding,
				IndentFor(type, text, rules),
				marker));
		}

		var root = await RootOf(document, cancellationToken);
		var updated = type.WithMembers(type.Members.InsertRange(index, prepared));

		return new Written(
			document,
			root.ReplaceNode(type, updated),
			marker,
			target.Signature,
			[.. parsed.SelectMany(NamesOf)],
			target.Symbol);
	}

	/// <summary>
	/// Ensures the imports the caller asked for, on the same file and before it is formatted.
	/// <para>
	/// Asked of the compilation rather than of the using list, because a namespace can be in scope
	/// three ways this file does not show: a global using, an implicit using from the SDK, or simply
	/// being the namespace the file is in. Adding a directive for one of those is IDE0005, which is a
	/// build error here -- so the check that looks unnecessary is the one that keeps the tool from
	/// breaking the build it was called to avoid.
	/// </para>
	/// </summary>
	private static async Task<Written> WithImportsAsync(
		Written written,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		if (request.Usings.Count == 0 || written.Root is not CompilationUnitSyntax root) return written;

		var document = written.Document;
		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);

		if (model is null || tree is null) return written;

		var rules = Whitespace.RulesFor(document.Project, tree, text);
		var style = UsingStyle.For(document.Project, tree, root, rules.LineEnding);

		var insertion = UsingDirectives.Ensure(root, model, request.Usings, style, cancellationToken);

		if (insertion.Added.Count > 0)
		{
			notices.Add($"Imported {string.Join(", ", insertion.Added)}.");
		}

		foreach (var covered in insertion.AlreadyInScope)
		{
			notices.Add($"Did not import {covered}.");
		}

		return written with { Root = insertion.Root };
	}

	/// <summary>
	/// Works out what would import the names the edit left unresolved, adds the ones with a single
	/// answer, and reports the rest.
	/// <para>
	/// The half <see cref="MissingImports"/> stops short of. Reporting the namespace and leaving the
	/// caller to add it is a round trip at exactly the moment they were promised there would not be
	/// one: the code was just written by this tool, and it does not compile.
	/// </para>
	/// </summary>
	private static async Task<Solution> ResolveImportsAsync(
		WorkspaceSnapshot snapshot,
		Solution solution,
		Written written,
		string path,
		IReadOnlyList<DiagnosticEntry> introduced,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var imports = await ResolvedImports.ForAsync(
			new WorkspaceSnapshot { Solution = solution, Revision = snapshot.Revision },
			introduced,
			path,
			Looked,
			cancellationToken);

		notices.AddRange(imports.Added);
		notices.AddRange(imports.Ambiguous);
		notices.AddRange(imports.Unresolved);

		return imports.AnythingToAdd
			? await ResolvedImports.ApplyAsync(solution, written.Document.Id, imports.Namespaces, cancellationToken)
			: solution;
	}

	/// <summary>
	/// Formats what was written, fixes its whitespace, and gives every project that compiles the
	/// file the same text.
	/// </summary>
	private static async Task<Finished> FinishAsync(Written written, CancellationToken cancellationToken)
	{
		var edited = written.Document.Project.Solution.WithDocumentSyntaxRoot(written.Document.Id, written.Root);

		var document = edited.GetDocument(written.Document.Id)
			?? throw new InvalidOperationException("The document being written left the solution mid-edit.");

		// Pointed at the written spans alone. The formatter honours .editorconfig but reindents
		// whatever it is given, so pointing it at the whole file would turn a one-member change into
		// a whole-file diff in any repository not already formatted to its own rules.
		var formatted = await Formatter.FormatAsync(document, written.Marker, cancellationToken: cancellationToken);

		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(written.Document.FilePath)} is not a C# source file.");
		}

		var nodes = root.GetAnnotatedNodes(written.Marker).ToArray();

		if (nodes.Length == 0)
		{
			throw new InvalidOperationException("The written members could not be found again after formatting.");
		}

		var span = TextSpan.FromBounds(
			nodes.Min(node => node.FullSpan.Start),
			nodes.Max(node => node.FullSpan.End));

		// Read before the whitespace pass, which can move every offset in the file by rewriting
		// line endings but cannot move a line: a line is a line either way.
		var line = text.Lines.GetLineFromPosition(nodes.Min(node => node.SpanStart)).LineNumber + 1;

		var rules = Whitespace.RulesFor(formatted.Project, tree, text);
		var final = Whitespace.Apply(root, text, rules, [span]);

		// Every project holding this file gets the same text. A linked document left on the old text
		// would answer the next question from a file that no longer exists, which is the staleness
		// this server exists to prevent.
		var solution = formatted.Project.Solution;

		foreach (var id in solution.GetDocumentIdsWithFilePath(written.Document.FilePath!))
		{
			solution = solution.WithDocumentText(id, final);
		}

		return new Finished(solution, line, [.. LiteralEndingNotices(root, span, text, rules)]);
	}

	/// <summary>
	/// The replacement, put where the old declaration was.
	/// <para>
	/// The trivia decides two things a caller would not think to say. The blank line and indentation
	/// that separated the old declaration from the member above it are layout rather than content,
	/// and they stay whatever the replacement looks like. The documentation comment is content: if
	/// the code carries one it replaces the old one, and if it does not the old one is kept rather
	/// than deleted, because a caller who never read the file cannot have meant to remove
	/// documentation it did not know was there -- and a silently deleted doc comment is invisible in
	/// everything except a review nobody does.
	/// </para>
	/// </summary>
	private static MemberDeclarationSyntax Positioned(
		MemberDeclarationSyntax replacement,
		MemberDeclarationSyntax existing,
		List<string> notices)
	{
		// Read before the attributes are carried over, because a declaration that has attributes keeps
		// its documentation comment above the first of them: the member's leading trivia is whichever
		// token comes first, and that changes under this call.
		var supplied = MemberSyntax.WithoutLeadingBlanks(replacement.GetLeadingTrivia());
		var existingTrivia = existing.GetLeadingTrivia();

		replacement = WithCarriedAttributes(replacement, existing, notices);

		if (!supplied.Any(MemberSyntax.IsComment))
		{
			if (existingTrivia.Any(MemberSyntax.IsComment))
			{
				notices.Add("Kept the comment above the declaration, since the code supplied none. "
					+ "Include one in the code to replace it.");
			}

			return replacement.WithLeadingTrivia(existingTrivia);
		}

		return replacement.WithLeadingTrivia(
			existingTrivia.TakeWhile(trivia => !MemberSyntax.IsComment(trivia)).Concat(supplied));
	}

	/// <summary>
	/// The attributes to write, on the documentation comment's rule: the replacement's when it declares
	/// any, and the old ones kept with a notice when it declares none.
	/// <para>
	/// The same reasoning with a sharper edge. A caller who never read the file cannot have meant to
	/// remove an attribute it did not know was there, and dropping one leaves valid C# that compiles
	/// and verifies clean while the member has quietly left whatever the attribute enrolled it in -- a
	/// tool off the MCP surface, a test out of the run. A caller who does mean to remove one writes the
	/// attributes it wants, or asks rose_set_attribute.
	/// </para>
	/// </summary>
	private static MemberDeclarationSyntax WithCarriedAttributes(
		MemberDeclarationSyntax replacement,
		MemberDeclarationSyntax existing,
		List<string> notices)
	{
		if (replacement.AttributeLists.Count > 0 || existing.AttributeLists.Count == 0) return replacement;

		var names = existing.AttributeLists
			.SelectMany(list => list.Attributes)
			.Select(attribute => $"[{attribute.Name}]");

		notices.Add($"Kept {string.Join(", ", names)} on the declaration, since the code supplied no "
			+ "attributes. Write the ones you want in the code to replace them, or ask rose_set_attribute "
			+ "to remove one.");

		// Stripped of the trivia they carried in the old file. The first of them held the declaration's
		// documentation comment and indentation, and both belong to whichever token ends up first here,
		// which the caller sets afterwards. Elastic markers leave the layout to the formatter.
		var carried = SyntaxFactory.List(existing.AttributeLists
			.Select(list => list
				.WithLeadingTrivia(SyntaxFactory.ElasticMarker)
				.WithTrailingTrivia(SyntaxFactory.ElasticMarker)));

		return replacement.WithLeadingTrivia().WithAttributeLists(carried);
	}

	/// <summary>
	/// Refuses to write over a declaration that declares more than one thing. <c>int a, b;</c> is
	/// one declaration and two members, so replacing it by naming either of them would delete the
	/// other -- which is the shape of failure this whole tool exists to make impossible.
	/// </summary>
	private static void GuardSharedDeclaration(DeclarationTarget target)
	{
		if (target.Declaration is not BaseFieldDeclarationSyntax field || field.Declaration.Variables.Count <= 1) return;

		var others = field.Declaration.Variables
			.Select(variable => variable.Identifier.Text)
			.Where(name => !string.Equals(name, target.Symbol.Name, StringComparison.Ordinal));

		throw new ArgumentException(
			$"{target.Symbol.Name} shares its declaration with {string.Join(", ", others)}, so writing over the "
				+ "declaration would write over all of them. Give them declarations of their own first.");
	}

	/// <summary>
	/// Refuses a member the type already declares, which is where a duplicate constant and a
	/// duplicated doc comment came from. Matched on the parameter types as written, so it cannot see
	/// that <c>int</c> and <c>System.Int32</c> are the same type -- a check that misses is a
	/// compiler error the same call reports back, while a check that over-refuses would block every
	/// legitimate overload.
	/// </summary>
	private static void GuardDuplicates(TypeDeclarationSyntax type, IReadOnlyList<MemberDeclarationSyntax> adding)
	{
		foreach (var member in adding)
		{
			foreach (var name in NamesOf(member))
			{
				var clash = type.Members.FirstOrDefault(existing =>
					NamesOf(existing).Contains(name, StringComparer.Ordinal) && SameParameters(existing, member));

				if (clash is null) continue;

				throw new ArgumentException(
					$"{type.Identifier.Text} already declares {name}, at line {LineOf(clash)}. Adding another would be "
						+ "a duplicate the compiler rejects; rose_replace_member writes over the one that is there.");
			}
		}
	}

	private static int PlacementIndex(TypeDeclarationSyntax type, MemberEditRequest request)
	{
		if (request.After is { Length: > 0 } after) return AnchorIndex(type, after) + 1;
		if (request.Before is { Length: > 0 } before) return AnchorIndex(type, before);

		return type.Members.Count;
	}

	private static int AnchorIndex(TypeDeclarationSyntax type, string name)
	{
		for (var index = 0; index < type.Members.Count; index++)
		{
			if (NamesOf(type.Members[index]).Contains(name, StringComparer.Ordinal)) return index;
		}

		var declared = type.Members.SelectMany(NamesOf).Distinct(StringComparer.Ordinal).ToArray();

		throw new ArgumentException(
			$"{type.Identifier.Text} declares no member called '{name}' to put this next to."
				+ (declared.Length == 0
					? " It declares no members at all, so leave after and before out."
					: $" It declares: {string.Join(", ", declared)}."));
	}

	private static IEnumerable<string> Notices(
		MemberEditRequest request,
		Verification verification,
		WriteOutcome outcome)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";

		// What the diff could not show. Said before the verification lines, because a caller reading a
		// result whose diff looks empty is asking about the write rather than about the compile.
		foreach (var notice in outcome.Notices) yield return notice;

		if (!verification.Ran)
		{
			if (outcome.ChangedFiles.Count > 0)
			{
				yield return "Nothing was compiled, so this says nothing about whether the code is sound. Pass "
					+ "verify=true, or ask rose_diagnostics.";
			}

			yield break;
		}

		foreach (var notice in verification.Notices) yield return notice;

		var compiled = string.Join(", ", verification.Projects);

		if (verification.Introduced.Count > Listed)
		{
			yield return $"Showing {Listed} of the {verification.Introduced.Count} errors this introduced.";
		}

		if (verification.TotalCount == 0) yield return $"{compiled} compiles clean.";

		var existing = verification.TotalCount - verification.Introduced.Count;

		if (existing > 0)
		{
			// The count is analyzer-inclusive wherever the edit wrote, and rose_diagnostics leaves
			// analyzers out by default -- so the bare advice sent a caller to a tool that answered 0
			// about 297 errors, which reads as the two disagreeing rather than as a default.
			yield return verification.AnalyzedProjects.Count == 0
				? $"{existing} error(s) in {compiled} were there before this edit; ask rose_diagnostics for those."
				: $"{existing} error(s) in {compiled} were there before this edit; ask rose_diagnostics with "
					+ "includeAnalyzers=true for those, since this count includes the analyzer diagnostics it "
					+ "leaves out by default.";
		}

		// The namespace itself, where the compilation could work it out. This is the answer the caller
		// needs next, and it used to be the point at which they went back to editing text.
		foreach (var suggestion in verification.Suggestions) yield return suggestion;

		if (verification.Introduced.Any(entry => Unresolved.Contains(entry.Id, StringComparer.Ordinal))
			&& verification.Suggestions.Count == 0)
		{
			yield return "A name that does not resolve is either something not written yet or a missing import, and "
				+ "nothing of that name is reachable from here -- so it is the first. rose_resolve_name searches for "
				+ "one by name; the usings argument on this tool imports what the code needs in the same call.";
		}

		// Said only where it can happen. A body cannot change a signature, and adding a member
		// cannot break a caller that was already compiling against the ones that were there.
		if (request.Kind == MemberEditKind.Replace)
		{
			yield return $"Only {compiled} was compiled. A changed signature breaks call sites in the projects that "
				+ "reference it, which this did not check -- rose_diagnostics with scope=solution does.";
		}
	}

	/// <summary>
	/// Where the part this tool replaces begins, or null for a declaration that has no such part.
	/// <para>
	/// An initialiser counts, and that is the whole of what makes a string constant reachable. Every
	/// tool description in this repository is the body of one, and changing a sentence in one had no
	/// tool at all: <c>rose_replace_member</c> re-emits the whole declaration, which for a fifty-line
	/// description means retyping fifty lines to change one. Only a declaration with a single variable
	/// qualifies, because <c>int a = 1, b = 2;</c> has two initialisers and naming either of them would
	/// have to pick.
	/// </para>
	/// </summary>
	private static int? BodyStart(MemberDeclarationSyntax declaration) => declaration switch
	{
		BaseMethodDeclarationSyntax method => ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.SpanStart,
		PropertyDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
		PropertyDeclarationSyntax { Initializer: { } initializer } => initializer.Value.SpanStart,
		IndexerDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
		BaseFieldDeclarationSyntax field => Initialiser(field)?.SpanStart,
		_ => null,
	};

	/// <summary>
	/// The single initialised variable's value, or null where there is not exactly one to name.
	/// </summary>
	private static ExpressionSyntax? Initialiser(BaseFieldDeclarationSyntax field) =>
		field.Declaration.Variables is [{ Initializer: { } initializer }] ? initializer.Value : null;

	/// <summary>
	/// True where what is being replaced is an initialiser rather than a body, so it goes back as an
	/// expression and a semicolon instead of being wrapped in braces or behind an arrow.
	/// </summary>
	private static bool IsInitialiser(MemberDeclarationSyntax declaration) => declaration switch
	{
		PropertyDeclarationSyntax { ExpressionBody: null, Initializer: not null } => true,
		BaseFieldDeclarationSyntax => true,
		_ => false,
	};

	private static string WhyNoBody(MemberDeclarationSyntax declaration) => declaration switch
	{
		BasePropertyDeclarationSyntax { AccessorList: not null } => " -- it has accessors, and each one has a body of its own",
		BaseFieldDeclarationSyntax { Declaration.Variables.Count: > 1 } =>
			" -- it declares more than one variable, so naming it does not say which initialiser to write",
		BaseFieldDeclarationSyntax => " -- it has no initialiser to replace",
		BaseMethodDeclarationSyntax => " -- it is abstract, extern, or one half of a partial",
		BaseTypeDeclarationSyntax => " -- it is a type",
		_ => string.Empty,
	};

	/// <summary>
	/// The supplied body in whichever of the three shapes it arrived in: a block, an expression
	/// body, or bare statements. All three are accepted because all three are what someone writing
	/// a body writes, and rejecting two of them would teach a caller to shape its code around a tool
	/// rather than around the code.
	/// </summary>
	private static string Body(string code)
	{
		var trimmed = code.Trim();

		if (trimmed.Length == 0) throw new ArgumentException("No body was supplied, so there is nothing to write.");
		if (trimmed.StartsWith('{')) return code;

		if (trimmed.StartsWith("=>", StringComparison.Ordinal))
		{
			return trimmed.EndsWith(';') ? code : $"{code.TrimEnd()};";
		}

		return $"{{\n{code}\n}}";
	}

	/// <summary>
	/// The keyword of the container a declaration sits in, so a snippet is parsed by the same rules
	/// the real container imposes. A top-level type has a namespace around it rather than a
	/// container, and parses as a nested one would.
	/// </summary>
	private static string KeywordAround(MemberDeclarationSyntax declaration) =>
		declaration.Parent is BaseTypeDeclarationSyntax container ? MemberSyntax.KeywordOf(container) : "class";

	/// <summary>
	/// The indentation the line at <paramref name="position"/> starts with, which is what a
	/// declaration written into that place has to line up with.
	/// </summary>
	private static string IndentAt(SourceText text, int position)
	{
		var line = text.Lines.GetLineFromPosition(position).ToString();

		return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
	}

	/// <summary>
	/// Where a new member's lines belong: level with the members already there, or one level in from
	/// the container when there are none to copy.
	/// </summary>
	private static string IndentFor(TypeDeclarationSyntax type, SourceText text, WhitespaceRules rules) =>
		type.Members.Count > 0
			? IndentAt(text, type.Members[0].SpanStart)
			: IndentAt(text, type.SpanStart) + rules.IndentUnit;

	/// <summary>
	/// Warns about a multi-line string in the written code whose line endings are not the file's.
	/// <para>
	/// Nothing here rewrites them, and that is correct: the endings inside a verbatim or raw literal
	/// are part of the string's value, which the compiler confirms -- the same raw literal written
	/// with CRLF and with LF are different strings. But it has a consequence worth saying out loud,
	/// because nothing else says it. A caller that writes a multi-line literal with bare newlines
	/// into a CRLF file gets a file that fails <c>dotnet format</c>, no build complains, and the
	/// obvious fix changes what the program says.
	/// </para>
	/// <para>
	/// Found three times in one session, writing this repository's own tool descriptions through
	/// these tools. <see cref="Whitespace.LiteralsDisagreeingWith"/> is the same detection
	/// <c>rose_format</c> runs over a whole file; only the sentence differs, because there the
	/// literal was already in the file rather than just written into it.
	/// </para>
	/// </summary>
	private static IEnumerable<string> LiteralEndingNotices(
		SyntaxNode root,
		TextSpan span,
		SourceText text,
		WhitespaceRules rules)
	{
		foreach (var line in Whitespace.LiteralsDisagreeingWith(root, text, rules, span))
		{
			yield return $"This file now fails dotnet format, and no build will report it: the multi-line string "
				+ $"at line {line} was written with line endings the file does not use. They were left exactly "
				+ "as supplied, because the endings inside a literal are part of the string and rewriting them "
				+ "changes the value. Write it with the file's own endings.";
		}
	}

	/// <summary>
	/// Whether a member already has a blank line above it, which it will have when whoever wrote the
	/// file put one there: the break belongs to the member below rather than the one above.
	/// </summary>
	private static bool StartsBlank(MemberDeclarationSyntax member) =>
		member.GetLeadingTrivia() is [var first, ..] && first.IsKind(SyntaxKind.EndOfLineTrivia);

	/// <summary>
	/// What a declaration is called, which for a field is every variable it declares. Used both to
	/// report what was written and to find the member an <c>after</c> or <c>before</c> names.
	/// </summary>
	private static IReadOnlyList<string> NamesOf(MemberDeclarationSyntax member) => member switch
	{
		BaseFieldDeclarationSyntax field => [.. field.Declaration.Variables.Select(variable => variable.Identifier.Text)],
		MethodDeclarationSyntax method => [method.Identifier.Text],
		PropertyDeclarationSyntax property => [property.Identifier.Text],
		EventDeclarationSyntax @event => [@event.Identifier.Text],
		ConstructorDeclarationSyntax constructor => [constructor.Identifier.Text],
		DestructorDeclarationSyntax destructor => [$"~{destructor.Identifier.Text}"],
		OperatorDeclarationSyntax @operator => [$"operator {@operator.OperatorToken.Text}"],
		ConversionOperatorDeclarationSyntax conversion => [$"operator {conversion.Type}"],
		IndexerDeclarationSyntax => ["this[]"],
		DelegateDeclarationSyntax @delegate => [@delegate.Identifier.Text],
		EnumMemberDeclarationSyntax value => [value.Identifier.Text],
		BaseTypeDeclarationSyntax type => [type.Identifier.Text],
		_ => [],
	};

	private static bool SameParameters(MemberDeclarationSyntax left, MemberDeclarationSyntax right)
	{
		var first = ParametersOf(left);
		var second = ParametersOf(right);

		if (first is null || second is null) return first is null && second is null;
		if (first.Count != second.Count) return false;

		return first
			.Zip(second)
			.All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
	}

	private static IReadOnlyList<string>? ParametersOf(MemberDeclarationSyntax member) => member switch
	{
		BaseMethodDeclarationSyntax method => Types(method.ParameterList),
		IndexerDeclarationSyntax indexer => Types(indexer.ParameterList),
		_ => null,
	};

	private static IReadOnlyList<string> Types(BaseParameterListSyntax parameters) =>
		[
			.. parameters.Parameters.Select(parameter =>
				(parameter.Type?.ToString() ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal)),
		];

	private static int LineOf(SyntaxNode node) =>
		node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

	private static async Task<SyntaxNode> RootOf(Document document, CancellationToken cancellationToken) =>
		await document.GetSyntaxRootAsync(cancellationToken)
			?? throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");

	/// <summary>The edit, ready to be formatted: which document, the new root, and what to call it.</summary>
	private sealed record Written(
		Document Document,
		SyntaxNode Root,
		SyntaxAnnotation Marker,
		string Symbol,
		IReadOnlyList<string> Members,
		ISymbol? Reaches);

	private sealed record Finished(Solution Solution, int Line, IReadOnlyList<string> Notices);

	/// <summary>
	/// Says that line endings in the supplied code were changed, because a diff cannot: a terminator
	/// is not line content, and inside a literal it is part of what the string says.
	/// </summary>
	private static string RewrittenEndings(int count, SourceText text) =>
		MemberSyntax.RewrittenEndings(count, LineEndings.Name(Whitespace.Dominant(text)));
}
