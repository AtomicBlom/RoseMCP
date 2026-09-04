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
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

		if (request.FilePaths.Count == 0) throw new ArgumentException("Name at least one file to format.");

		var solution = snapshot.Solution;
		var notices = new List<string>(snapshot.Notices);
		var formatted = new List<DocumentId>();
		var missing = new List<string>();

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

			solution = await FormatDocumentAsync(document, cancellationToken);
			formatted.Add(located.Id);
		}

		foreach (var path in missing)
		{
			notices.Add($"No document in the solution matches '{path}', so it was not formatted.");
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
					solution = await FormatDocumentAsync(document, cancellationToken);
				}
			}

			if (cleanup.Removed.Count > 0) notices.Add($"Removed {cleanup.Removed.Count} unnecessary using directive(s).");
		}

		// Read off the final text, so a literal the passes above left alone is reported once, against
		// the line it ends up on rather than the line it started at.
		notices.AddRange(await LiteralEndingNoticesAsync(solution, formatted, cancellationToken));

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 95);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, solution, request.Apply, noteSelfWrite, cancellationToken);

		// This tool needs these more than any other does. Rewriting line endings is the commonest
		// thing it is called for and the one change a unified diff cannot render, so without this a
		// successful call to fix a file full of LF reports changed files and an empty diff.
		notices.AddRange(outcome.Notices);

		if (!request.Apply) notices.Add("Preview only; nothing was written to disk.");
		if (outcome.ChangedFiles.Count == 0 && missing.Count == 0) notices.Add("Every file was already formatted.");

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
	/// Roslyn's formatter first, then the whitespace it does not own. Both are needed: the formatter
	/// reindents and moves braces according to .editorconfig but only rewrites the trivia it has
	/// reason to touch, which leaves every line it did not visit with whatever ending it arrived with.
	/// </summary>
	private static async Task<Solution> FormatDocumentAsync(Document document, CancellationToken cancellationToken)
	{
		var reformatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);

		var root = await reformatted.GetSyntaxRootAsync(cancellationToken);
		var text = await reformatted.GetTextAsync(cancellationToken);
		var tree = await reformatted.GetSyntaxTreeAsync(cancellationToken);

		if (root is null || tree is null) return reformatted.Project.Solution;

		var rules = Whitespace.RulesFor(reformatted.Project, tree, text);

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
	/// Grouped by file rather than one notice per literal, because the sentence explaining why it was
	/// left alone is the long part and it does not need saying five times.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<string>> LiteralEndingNoticesAsync(
		Solution solution,
		IReadOnlyList<DocumentId> documentIds,
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
			var rules = Whitespace.RulesFor(document.Project, tree, text);
			var lines = Whitespace.LiteralsDisagreeingWith(root, text, rules);
			if (lines.Count == 0) continue;

			var where = lines.Count == 1
				? $"the multi-line string at line {lines[0]}"
				: $"the multi-line strings at lines {string.Join(", ", lines)}";

			notices.Add($"{document.Name}: {where} hold line endings the file does not use, and were left "
				+ "alone -- a newline inside a literal is part of the string's value, so rewriting it changes "
				+ "what the program says. dotnet format will still ask for them, and no build will complain. "
				+ "Rewrite the literal with the file's own endings if the value allows it.");
		}

		return notices;
	}
}
