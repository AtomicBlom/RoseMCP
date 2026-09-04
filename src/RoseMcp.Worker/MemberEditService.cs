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

	public static async Task<MutationResult<MemberEditResult>> EditAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		MemberEditRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Locating {request.Symbol}", 0);

		var written = request.Kind switch
		{
			MemberEditKind.Add => await AddAsync(snapshot.Solution, request, cancellationToken),
			MemberEditKind.ReplaceBody => await ReplaceBodyAsync(snapshot.Solution, request, cancellationToken),
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

		// A preview is verified too: what an edit would break is the question a preview is asking.
		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling to see what the edit did", 70);

			var path = written.Document.FilePath!;

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				finished.Solution,
				EditVerification.ProjectsHolding(finished.Solution, path),
				path,
				cancellationToken);
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
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? finished.Solution : null;

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
			IndentAt(text, target.Declaration.SpanStart));

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
			[.. NamesOf(parsed[0])]);
	}

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

		var parsed = MemberSyntax.Parse(
			$"{head} {Body(request.Code)}",
			KeywordAround(declaration),
			target.Document.Project.ParseOptions,
			indent);

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
			[.. NamesOf(declaration)]);
	}

	private static async Task<Written> AddAsync(
		Solution solution,
		MemberEditRequest request,
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
			IndentFor(type, text, rules));

		GuardDuplicates(type, parsed);

		var index = PlacementIndex(type, request);

		var marker = new SyntaxAnnotation();
		var prepared = new List<MemberDeclarationSyntax>(parsed.Count);

		// The member being pushed down usually carries the blank line above it as its own leading
		// trivia, in which case it is already separated and adding another gives two.
		var followerIsSeparated = index >= type.Members.Count || StartsBlank(type.Members[index]);

		for (var position = 0; position < parsed.Count; position++)
		{
			prepared.Add(Prepared(
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
			[.. parsed.SelectMany(NamesOf)]);
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
		var supplied = WithoutLeadingBlanks(replacement.GetLeadingTrivia());
		var existingTrivia = existing.GetLeadingTrivia();

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
	/// A member as it will read in the file: indented for where it is going, separated from its
	/// neighbours by a blank line, ending its own line, and marked so the formatter and the
	/// whitespace pass know which lines are new.
	/// <para>
	/// The indentation goes on as leading trivia rather than being left to the formatter, and that is
	/// what makes this path behave like the replace path. Given a member whose first line is already
	/// indented, the formatter leaves the lines it has no rule about -- a wrapped parameter list --
	/// exactly where they are, which is where the shift put them. Given one with no leading
	/// whitespace it recomputes the indentation itself, and then the shift and the formatter both
	/// apply, and every wrapped line lands a level too deep. Measured: writing this very method
	/// through the tool put its attribute arguments at three tabs.
	/// </para>
	/// <para>
	/// The blank line is added here rather than left to the formatter, which reindents and moves
	/// braces but never inserts one between members -- so a member appended without it lands flush
	/// against the one above.
	/// </para>
	/// </summary>
	private static MemberDeclarationSyntax Prepared(
		MemberDeclarationSyntax member,
		bool blankBefore,
		bool blankAfter,
		string lineEnding,
		string indent,
		SyntaxAnnotation marker)
	{
		var newLine = SyntaxFactory.EndOfLine(lineEnding);

		IEnumerable<SyntaxTrivia> leading = WithoutLeadingBlanks(member.GetLeadingTrivia());

		if (indent.Length > 0) leading = [SyntaxFactory.Whitespace(indent), .. leading];
		if (blankBefore) leading = [newLine, .. leading];

		var trailing = member.GetTrailingTrivia();
		if (trailing.Count == 0 || !trailing.Last().IsKind(SyntaxKind.EndOfLineTrivia)) trailing = trailing.Add(newLine);
		if (blankAfter) trailing = trailing.Add(newLine);

		return member
			.WithLeadingTrivia(leading)
			.WithTrailingTrivia(trailing)
			.WithAdditionalAnnotations(marker);
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

		if (!verification.Ran)
		{
			if (outcome.ChangedFiles.Count > 0)
			{
				yield return "Nothing was compiled, so this says nothing about whether the code is sound. Pass "
					+ "verify=true, or ask rose_diagnostics.";
			}

			yield break;
		}

		var compiled = string.Join(", ", verification.Projects);

		if (verification.Introduced.Count > Listed)
		{
			yield return $"Showing {Listed} of the {verification.Introduced.Count} errors this introduced.";
		}

		if (verification.TotalCount == 0) yield return $"{compiled} compiles clean.";

		var existing = verification.TotalCount - verification.Introduced.Count;

		if (existing > 0)
		{
			yield return $"{existing} error(s) in {compiled} were there before this edit; ask rose_diagnostics for those.";
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
	/// Where the body starts, or nothing when the member has no single body to replace. A property
	/// with accessors has one body each and an abstract method has none, and both are better said
	/// than guessed at.
	/// </summary>
	private static int? BodyStart(MemberDeclarationSyntax declaration) => declaration switch
	{
		BaseMethodDeclarationSyntax method => ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.SpanStart,
		PropertyDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
		IndexerDeclarationSyntax { ExpressionBody: { } arrow } => arrow.SpanStart,
		_ => null,
	};

	private static string WhyNoBody(MemberDeclarationSyntax declaration) => declaration switch
	{
		BasePropertyDeclarationSyntax { AccessorList: not null } => " -- it has accessors, and each one has a body of its own",
		BaseFieldDeclarationSyntax => " -- a field has an initialiser rather than a body",
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
	/// these tools.
	/// </para>
	/// </summary>
	private static IEnumerable<string> LiteralEndingNotices(
		SyntaxNode root,
		TextSpan span,
		SourceText text,
		WhitespaceRules rules)
	{
		foreach (var node in root.DescendantNodes())
		{
			if (node is not (LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)) continue;
			if (!span.IntersectsWith(node.Span)) continue;

			var written = node.ToString();
			if (!written.Contains('\n', StringComparison.Ordinal)) continue;
			if (Whitespace.Dominant(SourceText.From(written)) == rules.LineEnding) continue;

			var line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;

			yield return $"The multi-line string at line {line} was written with line endings the file does not "
				+ "use. They were left exactly as supplied, because the endings inside a literal are part of "
				+ "the string -- but dotnet format will ask for them to change, and changing them changes the "
				+ "value. Write it with the file's own endings.";
		}
	}

	/// <summary>
	/// Whether a member already has a blank line above it, which it will have when whoever wrote the
	/// file put one there: the break belongs to the member below rather than the one above.
	/// </summary>
	private static bool StartsBlank(MemberDeclarationSyntax member) =>
		member.GetLeadingTrivia() is [var first, ..] && first.IsKind(SyntaxKind.EndOfLineTrivia);

	private static IReadOnlyList<SyntaxTrivia> WithoutLeadingBlanks(SyntaxTriviaList trivia) =>
		[
			.. trivia.SkipWhile(candidate =>
				candidate.Kind() is SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia),
		];

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
		IReadOnlyList<string> Members);

	private sealed record Finished(Solution Solution, int Line, IReadOnlyList<string> Notices);
}
