using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Moves a top-level type into a file of its own.
/// <para>
/// Works on the file's text rather than by rewriting its syntax tree. A rewrite renormalises
/// trivia, so in a repository that builds with EnforceCodeStyleInBuild the move would only apply
/// cleanly if it also happened to reproduce the house formatting exactly -- tabs, brace placement
/// and blank lines included. Copying the declaration's own lines verbatim keeps whatever the file
/// already had, which is the one formatting guaranteed to be acceptable.
/// </para>
/// </summary>
public static class MoveTypeService
{
	public static async Task<MutationResult<MoveTypeResult>> MoveAsync(
		WorkspaceSnapshot snapshot,
		MoveTypeRequest request,
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

		progress?.Report($"Locating {request.TypeName}", 0);

		var document = SymbolLocator.FindDocument(snapshot.Solution, request.FilePath)
			?? throw new ArgumentException($"No document in the solution matches '{request.FilePath}'.");

		var sourcePath = document.FilePath ?? request.FilePath;

		if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
		{
			throw new InvalidOperationException($"{Path.GetFileName(sourcePath)} is not a C# source file.");
		}

		var text = await document.GetTextAsync(cancellationToken);
		var moving = Select(root, request.TypeName, sourcePath);
		var siblings = Siblings(moving);

		if (siblings.Count == 1) throw OnlyType(moving, sourcePath);

		var targetPath = ResolveTarget(request, sourcePath, moving);
		Refuse(snapshot.Solution, sourcePath, targetPath);

		progress?.Report($"Extracting {NameOf(moving)}", 20);

		var region = Region(text, moving);
		GuardDirectives(text, region, moving, sourcePath);

		var lineEnding = LineEnding(text);
		var targetText = BuildTarget(text, moving, region, lineEnding);
		var remainingText = BuildRemainder(text, region, lineEnding);

		var solution = snapshot.Solution.WithDocumentText(document.Id, SourceText.From(remainingText));

		var targetId = DocumentId.CreateNewId(document.Project.Id, Path.GetFileName(targetPath));
		solution = solution.AddDocument(
			targetId,
			Path.GetFileName(targetPath),
			SourceText.From(targetText),
			document.Folders,
			targetPath);

		// Costs a compilation of the project, which is the price of asking the compiler rather than
		// guessing which imports each half still needs.
		progress?.Report("Checking for using directives the split made unnecessary", 55);
		var cleanup = await UnnecessaryUsings.RemoveAsync(solution, [document.Id, targetId], cancellationToken);

		progress?.Report(request.Apply ? "Writing both files" : "Building the diff", 90);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, cleanup.Solution, request.Apply, noteSelfWrite, cancellationToken);

		var result = new MoveTypeResult
		{
			Revision = snapshot.Revision,
			TypeName = NameOf(moving),
			SourcePath = sourcePath,
			TargetPath = targetPath,
			Applied = request.Apply,
			RemovedUsings = cleanup.Removed,
			ChangedFiles = outcome.ChangedFiles,
			Diff = outcome.Diff,
			Notices = Notices(snapshot, request, moving, sourcePath, targetPath),
		};

		return new MutationResult<MoveTypeResult>(result, request.Apply ? cleanup.Solution : null);
	}

	/// <summary>
	/// The declaration to move. Named rather than pointed at, so the failures worth being clear
	/// about are "no such type here" and "more than one of them".
	/// </summary>
	private static MemberDeclarationSyntax Select(CompilationUnitSyntax root, string typeName, string sourcePath)
	{
		var name = typeName.Trim();
		var bare = name.Contains('<', StringComparison.Ordinal) ? name[..name.IndexOf('<', StringComparison.Ordinal)] : name;

		var all = TopLevelTypes(root);
		var matches = all.Where(member => NameOf(member) == bare).ToArray();

		if (matches.Length == 1) return matches[0];

		var file = Path.GetFileName(sourcePath);

		if (matches.Length > 1)
		{
			throw new InvalidOperationException(
				$"{file} declares '{bare}' {matches.Length} times -- partial declarations, or the same name at "
					+ "different arities. Moving one of them is ambiguous, so do it by hand.");
		}

		var available = all.Count == 0
			? "It declares no top-level types."
			: "It declares: " + string.Join(", ", all.Select(NameOf));

		var nested = root.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>()
			.Any(type => type.Identifier.Text == bare);

		var hint = nested
			? $" '{bare}' is nested inside another type there; a nested type cannot move to its own file "
				+ "without splitting its container into a partial."
			: string.Empty;

		throw new ArgumentException($"No top-level type called '{bare}' in {file}. {available}.{hint}");
	}

	/// <summary>
	/// Types declared directly in the file or in one of its namespaces. Nested types are excluded
	/// deliberately: moving one out would leave its container needing to become partial, which is a
	/// different and much larger edit than the caller asked for.
	/// </summary>
	private static IReadOnlyList<MemberDeclarationSyntax> TopLevelTypes(CompilationUnitSyntax root) =>
	[
		.. root.Members.SelectMany(member => member switch
		{
			BaseNamespaceDeclarationSyntax @namespace => @namespace.Members.Where(IsType),
			_ => IsType(member) ? [member] : Array.Empty<MemberDeclarationSyntax>(),
		}),
	];

	/// <summary>The types sharing the declaration's immediate parent, itself included.</summary>
	private static IReadOnlyList<MemberDeclarationSyntax> Siblings(MemberDeclarationSyntax moving) => moving.Parent switch
	{
		BaseNamespaceDeclarationSyntax @namespace => [.. @namespace.Members.Where(IsType)],
		CompilationUnitSyntax root => [.. root.Members.Where(IsType)],
		_ => [moving],
	};

	private static bool IsType(MemberDeclarationSyntax member) =>
		member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

	private static string NameOf(MemberDeclarationSyntax member) => member switch
	{
		BaseTypeDeclarationSyntax type => type.Identifier.Text,
		DelegateDeclarationSyntax @delegate => @delegate.Identifier.Text,
		_ => string.Empty,
	};

	/// <summary>
	/// Moving the last type out of a file leaves a file holding nothing but its usings, and deleting
	/// files is a bigger hammer than a move should reach for. Renaming the file is what was meant.
	/// </summary>
	private static InvalidOperationException OnlyType(MemberDeclarationSyntax moving, string sourcePath)
	{
		var where = moving.Parent is BaseNamespaceDeclarationSyntax @namespace
			? $"namespace {@namespace.Name} in {Path.GetFileName(sourcePath)}"
			: Path.GetFileName(sourcePath);

		return new InvalidOperationException(
			$"'{NameOf(moving)}' is the only type in {where}, so moving it out would leave an empty file. "
				+ "Rename the file instead.");
	}

	private static string ResolveTarget(MoveTypeRequest request, string sourcePath, MemberDeclarationSyntax moving)
	{
		var directory = Path.GetDirectoryName(sourcePath) ?? ".";

		if (string.IsNullOrWhiteSpace(request.TargetPath))
		{
			return Path.GetFullPath(Path.Combine(directory, NameOf(moving) + ".cs"));
		}

		// A relative target is relative to the file being split, not to the worker's process, which
		// is somewhere the caller has no reason to know about.
		var supplied = Path.IsPathRooted(request.TargetPath)
			? request.TargetPath
			: Path.Combine(directory, request.TargetPath);

		return Path.GetFullPath(supplied);
	}

	/// <summary>
	/// Refuses to write over anything. Appending a type to an existing file is a reasonable thing to
	/// want and a different operation; silently merging two files would not be either of them.
	/// </summary>
	private static void Refuse(Solution solution, string sourcePath, string targetPath)
	{
		if (string.Equals(Path.GetFullPath(sourcePath), targetPath, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The target is the file the type is already in.");
		}

		if (File.Exists(targetPath))
		{
			throw new InvalidOperationException(
				$"{targetPath} already exists. Pick a target that does not, or move the type there by hand.");
		}

		if (SymbolLocator.FindDocument(solution, targetPath) is not null)
		{
			throw new InvalidOperationException($"The solution already has a document at {targetPath}.");
		}
	}

	/// <summary>
	/// The lines the declaration occupies, widened upwards over the comments written for it. Its
	/// attributes need no such treatment: the syntax node already contains them.
	/// </summary>
	private static (int First, int Last) Region(SourceText text, MemberDeclarationSyntax moving)
	{
		var first = text.Lines.GetLineFromPosition(moving.SpanStart).LineNumber;
		var last = text.Lines.GetLineFromPosition(moving.Span.End).LineNumber;

		while (first > 0)
		{
			var previous = text.Lines[first - 1].ToString().TrimStart();
			var isComment = previous.StartsWith("//", StringComparison.Ordinal);

			if (!isComment) break;

			first--;
		}

		return (first, last);
	}

	/// <summary>
	/// Declines anything conditionally compiled. A type inside #if or #region moves out of the
	/// thing that was conditioning it, which changes what gets compiled rather than just where it
	/// lives -- and the caller almost certainly did not mean that.
	/// </summary>
	private static void GuardDirectives(
		SourceText text,
		(int First, int Last) region,
		MemberDeclarationSyntax moving,
		string sourcePath)
	{
		var inside = Enumerable.Range(region.First, region.Last - region.First + 1)
			.Any(line => text.Lines[line].ToString().TrimStart().StartsWith('#'));

		if (!inside && !moving.GetLeadingTrivia().Any(trivia => trivia.IsDirective)) return;

		throw new InvalidOperationException(
			$"'{NameOf(moving)}' in {Path.GetFileName(sourcePath)} is mixed up with preprocessor directives. "
				+ "Moving it could change what is compiled, so do this one by hand.");
	}

	/// <summary>
	/// The new file: everything above the first type in the original -- header comments, usings, the
	/// namespace as it was written -- then the declaration's own lines, then whatever closes the
	/// namespace. Copied verbatim, so the result is indented and spaced exactly as the source was.
	/// </summary>
	private static string BuildTarget(
		SourceText text,
		MemberDeclarationSyntax moving,
		(int First, int Last) region,
		string lineEnding)
	{
		var builder = new StringBuilder();
		var prologue = PrologueEnd(text, moving);

		for (var line = 0; line <= prologue; line++)
		{
			builder.Append(text.Lines[line].ToString()).Append(lineEnding);
		}

		if (prologue >= 0) builder.Append(lineEnding);

		for (var line = region.First; line <= region.Last; line++)
		{
			builder.Append(text.Lines[line].ToString()).Append(lineEnding);
		}

		// A block namespace has to be closed again. Its own closing line is copied rather than
		// written, so a file indenting with tabs does not come back indenting with spaces.
		if (moving.Parent is NamespaceDeclarationSyntax block)
		{
			var closing = text.Lines.GetLineFromPosition(block.CloseBraceToken.SpanStart);
			builder.Append(closing.ToString()).Append(lineEnding);
		}

		return builder.ToString();
	}

	/// <summary>
	/// The last line of the file's preamble: the semicolon of a file-scoped namespace, the opening
	/// brace of a block one, or the final using when the file has no namespace at all.
	/// </summary>
	private static int PrologueEnd(SourceText text, MemberDeclarationSyntax moving)
	{
		switch (moving.Parent)
		{
			case FileScopedNamespaceDeclarationSyntax fileScoped:
				return text.Lines.GetLineFromPosition(fileScoped.SemicolonToken.SpanStart).LineNumber;

			case NamespaceDeclarationSyntax block:
				return text.Lines.GetLineFromPosition(block.OpenBraceToken.SpanStart).LineNumber;

			default:
				var root = (CompilationUnitSyntax)moving.Parent!;
				var last = root.Usings.Cast<SyntaxNode>().Concat(root.Externs).LastOrDefault();

				return last is null ? -1 : text.Lines.GetLineFromPosition(last.Span.End).LineNumber;
		}
	}

	/// <summary>
	/// The source file with those lines gone, and without the double blank line that removing a
	/// member from the middle of a file otherwise leaves behind.
	/// </summary>
	private static string BuildRemainder(SourceText text, (int First, int Last) region, string lineEnding)
	{
		var lines = new List<string>();

		for (var line = 0; line < text.Lines.Count; line++)
		{
			if (line >= region.First && line <= region.Last) continue;

			lines.Add(text.Lines[line].ToString());
		}

		var seam = region.First;
		while (seam > 0 && seam < lines.Count && IsBlank(lines[seam - 1]) && IsBlank(lines[seam]))
		{
			lines.RemoveAt(seam);
		}

		while (lines.Count > 0 && IsBlank(lines[^1]))
		{
			lines.RemoveAt(lines.Count - 1);
		}

		return string.Join(lineEnding, lines) + lineEnding;
	}

	private static bool IsBlank(string line) => line.Trim().Length == 0;

	/// <summary>
	/// Whatever the file already uses. Writing a CRLF repository's files with bare newlines is the
	/// kind of change that turns a one-line diff into a whole-file one.
	/// </summary>
	private static string LineEnding(SourceText text)
	{
		if (text.Lines.Count == 0) return Environment.NewLine;

		var first = text.Lines[0];
		var breakLength = first.SpanIncludingLineBreak.Length - first.Span.Length;

		return breakLength switch
		{
			2 => "\r\n",
			1 => text.ToString(new TextSpan(first.End, 1)),
			_ => Environment.NewLine,
		};
	}

	private static IReadOnlyList<string> Notices(
		WorkspaceSnapshot snapshot,
		MoveTypeRequest request,
		MemberDeclarationSyntax moving,
		string sourcePath,
		string targetPath)
	{
		var notices = new List<string>(snapshot.Notices);

		if (!request.Apply) notices.Add("Preview only; nothing was written to disk.");

		var movedFolder = !string.Equals(
			Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
			Path.GetDirectoryName(targetPath),
			StringComparison.OrdinalIgnoreCase);

		if (movedFolder && moving.Parent is BaseNamespaceDeclarationSyntax @namespace)
		{
			notices.Add($"{NameOf(moving)} keeps namespace {@namespace.Name} while moving to another folder, "
				+ "so the namespace no longer matches where the file lives.");
		}

		return notices;
	}
}
