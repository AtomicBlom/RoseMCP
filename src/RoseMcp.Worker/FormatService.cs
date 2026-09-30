using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Formats files the way this repository's own .editorconfig says, using Roslyn's formatter for the
/// syntax and <see cref="Whitespace"/> for the three things it leaves alone.
/// <para>
/// This exists because writing C# and getting its whitespace right are separate skills, and a caller
/// that is good at the first routinely fails the second: spaces where the repository wants tabs, LF
/// where it wants CRLF, a brace on the wrong line. In a repository that escalates IDE0055 to an
/// error, each of those is a failed build rather than a tidiness question -- and the fix is not a
/// judgement call, it is written down in a file the compiler already reads.
/// </para>
/// </summary>
public static class FormatService
{
	public static async Task<MutationResult<FormatResult>> FormatAsync(
		WorkspaceSnapshot snapshot,
		FormatRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		if (request.FilePaths.Count == 0) throw new ArgumentException("Name at least one file to format.");

		var solution = snapshot.Solution;
		var notices = new List<string>(snapshot.Notices);
		var formatted = new List<DocumentId>();
		var missing = new List<string>();
		var layouts = new Dictionary<DocumentId, WhitespaceRules>();

		for (var index = 0; index < request.FilePaths.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var path = request.FilePaths[index];

			progress?.Report(
				$"Formatting {Path.GetFileName(path)} ({index + 1}/{request.FilePaths.Count})",
				90.0 * index / request.FilePaths.Count);

			if (SymbolLocator.FindDocument(solution, path) is not { } located)
			{
				missing.Add(path);
				continue;
			}

			// Re-fetched from the solution being built up, so formatting several files in one call
			// accumulates rather than each one starting from the original snapshot.
			var document = solution.GetDocument(located.Id);
			if (document is null) continue;

			// Read from the file as this call found it, so formatting it twice in one call, or once more after
			// the usings come out, asks the same question of the same text.
			var rules = layouts.GetValueOrDefault(located.Id)
				?? await Whitespace.RulesForAsync(snapshot.Solution.GetDocument(located.Id) ?? document, cancellationToken);

			layouts[located.Id] = rules;

			solution = await FormatDocumentAsync(document, rules, cancellationToken);
			formatted.Add(located.Id);
		}

		foreach (var path in missing)
		{
			notices.Add($"No project in this solution compiles '{path}', so it was not formatted.");
		}

		if (request.RemoveUnusedUsings && formatted.Count > 0)
		{
			progress?.Report("Removing unnecessary using directives", 90);

			var cleanup = await UnnecessaryUsings.RemoveAsync(solution, formatted, cancellationToken);
			solution = cleanup.Solution;

			// Removing a using changes indentation of nothing, but it can leave the blank line the
			// group used to occupy, so the whitespace pass runs again over what it touched.
			foreach (var documentId in formatted)
			{
				if (solution.GetDocument(documentId) is { } document)
				{
					solution = await FormatDocumentAsync(document, layouts[documentId], cancellationToken);
				}
			}

			if (cleanup.Removed.Count > 0) notices.Add($"Removed {cleanup.Removed.Count} unnecessary using directive(s).");
		}

		// Read off the final text, so a literal the passes above left alone is reported once, against
		// the line it ends up on rather than the line it started at.
		var literalEndings = await LiteralEndingNoticesAsync(solution, formatted, layouts, cancellationToken);

		notices.AddRange(literalEndings);
		notices.AddRange(UndeclaredIndentation(solution, formatted, layouts));

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 95);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, solution, request.Apply, noteSelfWrite, cancellationToken);

		// This tool needs these more than any other does. Rewriting line endings is the commonest
		// thing it is called for and the one change a unified diff cannot render, so without this a
		// successful call to fix a file full of LF reports changed files and an empty diff.
		notices.AddRange(outcome.Notices);

		if (!request.Apply) notices.Add("Preview only; nothing was written to disk.");
		if (outcome.ChangedFiles.Count == 0 && missing.Count == 0)
		{
			// Never "already formatted" while a literal above says dotnet format will reject the file.
			// The two sentences contradict each other, a caller reads the headline, and the headline is
			// the one that restates the failure this tool exists to remove. Which literal and why it was
			// left alone is already said; this only has to stop claiming the opposite.
			notices.Add(literalEndings.Count == 0
				? "Every file was already formatted."
				: "Nothing needed reformatting, and dotnet format will still reject the literal endings named "
					+ "above -- a failed build wherever IDE0055 is an error.");
		}

		var result = new FormatResult
		{
			Revision = snapshot.Revision,
			FilesInspected = formatted.Count,
			ChangedFiles = outcome.ChangedFiles,
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? solution : null;

		return new MutationResult<FormatResult>(result, changed);
	}

	/// <summary>
	/// Roslyn's formatter first, then the whitespace it does not own, both to <paramref name="rules"/>.
	/// Both are needed: the formatter reindents and moves braces but only rewrites the trivia it has
	/// reason to touch, which leaves every line it did not visit with whatever ending it arrived with.
	/// </summary>
	private static async Task<Solution> FormatDocumentAsync(
		Document document,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		var options = await Whitespace.FormattingOptionsAsync(document, rules, cancellationToken);
		var reformatted = await Formatter.FormatAsync(document, options, cancellationToken);

		var root = await reformatted.GetSyntaxRootAsync(cancellationToken);
		var text = await reformatted.GetTextAsync(cancellationToken);

		if (root is null) return reformatted.Project.Solution;

		return reformatted.Project.Solution.WithDocumentText(reformatted.Id, Whitespace.Apply(root, text, rules));
	}

	/// <summary>
	/// One notice per file holding a multi-line literal whose line endings are not the file's.
	/// <para>
	/// This is the half of the promise the formatter cannot keep. <c>rose_format</c> tells a caller
	/// their file is formatted, and its own description tells them to call it after writing C# by any
	/// other means -- and then <c>dotnet format</c> fails the same file on ENDOFLINE inside a raw
	/// literal, at the build, which is exactly the shape of failure this whole surface exists to
	/// remove. Rewriting it is not the answer, because a newline inside a literal is part of the
	/// string's value: what is missing is that the tool ever said so.
	/// </para>
	/// <para>
	/// The sentence itself is <see cref="Whitespace.LiteralEndingNotice"/>, shared with
	/// <c>rose_add_file</c>, which is the other tool a caller reaches for after writing a file full of
	/// literals and has to say the same thing about it.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<string>> LiteralEndingNoticesAsync(
		Solution solution,
		IReadOnlyList<DocumentId> documentIds,
		IReadOnlyDictionary<DocumentId, WhitespaceRules> layouts,
		CancellationToken cancellationToken)
	{
		var notices = new List<string>();

		foreach (var documentId in documentIds)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var document = solution.GetDocument(documentId);
			if (document is null) continue;

			var root = await document.GetSyntaxRootAsync(cancellationToken);
			var tree = await document.GetSyntaxTreeAsync(cancellationToken);
			if (root is null || tree is null) continue;

			var text = await document.GetTextAsync(cancellationToken);
			var rules = layouts[documentId];

			if (Whitespace.LiteralEndingNotice(root, text, rules, document.Name) is { } notice) notices.Add(notice);
		}

		return notices;
	}

	/// <summary>
	/// What to say about files no .editorconfig says how to indent, where what they were checked against
	/// is not the four spaces <c>dotnet format</c> would use.
	/// <para>
	/// Nothing declaring an indentation, this keeps a file indented the way it already is, which is what
	/// stops a format from re-indenting a file into a convention nobody chose. <c>dotnet format</c> has no
	/// such fallback: it reads .editorconfig alone and, finding nothing there, indents with Roslyn's four
	/// spaces. Saying only that a file was already formatted would then be true of what this checked and
	/// contradicted by the check CI runs, which is the answer a caller cannot tell from a correct one.
	/// </para>
	/// </summary>
	private static IEnumerable<string> UndeclaredIndentation(
		Solution solution,
		IReadOnlyList<DocumentId> documentIds,
		IReadOnlyDictionary<DocumentId, WhitespaceRules> layouts)
	{
		var undeclared = documentIds
			.Distinct()
			.Select(id => (Name: solution.GetDocument(id)?.Name, Rules: layouts[id]))
			.Where(file => file.Name is not null
				&& file.Rules.IndentFrom is LayoutSource.File or LayoutSource.Neighbours
				&& file.Rules.IndentUnit != "    ")
			.GroupBy(file => (file.Rules.IndentUnit, file.Rules.IndentFrom));

		foreach (var group in undeclared)
		{
			var names = group.Select(file => file.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			var named = names.Length <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Length - 3} more";
			var unit = group.Key.IndentUnit == "\t" ? "tabs" : $"{group.Key.IndentUnit.Length} spaces";

			var where = group.Key.IndentFrom == LayoutSource.File
				? $"the {unit} already there"
				: $"the {unit} the files beside them use";

			yield return $"{named}: no .editorconfig says how to indent, so this checked against {where}. dotnet format "
				+ "reads only .editorconfig and would want four spaces; an indent_style there settles it for both.";
		}
	}
}
