using System.Text;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// Writes solution changes to disk and reports them as a unified diff.
/// <para>
/// Deliberately does not use Workspace.TryApplyChanges. The session owns its own snapshot rather
/// than the workspace's, so TryApplyChanges has nothing to apply against; and writing the files
/// here is what lets the watcher be told which writes were ours before they land, instead of
/// bouncing them back as external edits.
/// </para>
/// </summary>
public static class SolutionWriter
{
	public static async Task<WriteOutcome> ApplyAsync(
		Solution before,
		Solution after,
		bool write,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken)
	{
		var changed = new List<string>();
		var diff = new StringBuilder();
		var retyped = new List<(string Path, int Lines, string To)>();

		foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
		{
			// Added documents come first: a split writes the new file before the old one shrinks, so
			// a reader that catches the pair mid-write sees the type twice rather than not at all.
			foreach (var documentId in projectChange.GetAddedDocuments())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var added = after.GetDocument(documentId);
				if (added?.FilePath is not { Length: > 0 } path) continue;

				var text = (await added.GetTextAsync(cancellationToken)).ToString();

				changed.Add(path);
				diff.Append(UnifiedDiff.RenderNewFile(path, text));

				if (!write) continue;

				noteSelfWrite?.Invoke(path);

				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, text, cancellationToken);
			}

			foreach (var documentId in projectChange.GetChangedDocuments())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var oldDocument = before.GetDocument(documentId);
				var newDocument = after.GetDocument(documentId);
				if (oldDocument?.FilePath is not { Length: > 0 } path || newDocument is null) continue;

				var oldText = (await oldDocument.GetTextAsync(cancellationToken)).ToString();
				var newText = (await newDocument.GetTextAsync(cancellationToken)).ToString();
				if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

				changed.Add(path);
				diff.Append(UnifiedDiff.Render(path, oldText, newText));

				// Recorded separately because the diff above cannot carry it: a terminator is not line
				// content, so the change this most often makes shows there as nothing at all.
				if (LineEndings.Changed(oldText, newText) is { } moved) retyped.Add((path, moved.Lines, moved.To));

				if (!write) continue;

				noteSelfWrite?.Invoke(path);
				await File.WriteAllTextAsync(path, newText, cancellationToken);
			}
		}

		return new WriteOutcome
		{
			ChangedFiles = changed,
			Diff = diff.ToString(),
			Notices = Retyped(retyped),
		};
	}

	/// <summary>
	/// The line-ending changes as sentences, grouped by what they were changed to.
	/// <para>
	/// Said because the diff cannot say it. Rewriting a file's terminators changes no line's content,
	/// so it produces no hunk -- and a result carrying five changed files beside an empty diff reads
	/// exactly like a call that did nothing, in the one situation where it did the most.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> Retyped(IReadOnlyList<(string Path, int Lines, string To)> retyped)
	{
		if (retyped.Count == 0) return [];

		return
		[
			.. retyped
				.GroupBy(entry => entry.To, StringComparer.Ordinal)
				.OrderBy(group => group.Key, StringComparer.Ordinal)
				.Select(group =>
					$"Rewrote {group.Sum(entry => entry.Lines)} line ending(s) to {group.Key} in {Named(group)}. "
						+ "A terminator is not line content, so none of that shows in the diff."),
		];
	}

	/// <summary>
	/// The files by name while there are few enough to read, and a count past that. A caller with
	/// forty reformatted files wants the number; a caller with two wants to know which two.
	/// </summary>
	private static string Named(IEnumerable<(string Path, int Lines, string To)> entries)
	{
		var names = entries.Select(entry => Path.GetFileName(entry.Path)).ToArray();

		return names.Length <= 3 ? string.Join(", ", names) : $"{names.Length} file(s)";
	}
}
