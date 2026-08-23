using System.Text;

using Microsoft.CodeAnalysis;

namespace RoslynMcp.Worker;

/// <summary>What a set of solution changes did, or would do.</summary>
public sealed record WriteOutcome
{
	public required IReadOnlyList<string> ChangedFiles { get; init; }

	public required string Diff { get; init; }
}

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

		foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
		{
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

				if (!write) continue;

				noteSelfWrite?.Invoke(path);
				await File.WriteAllTextAsync(path, newText, cancellationToken);
			}
		}

		return new WriteOutcome
		{
			ChangedFiles = changed,
			Diff = diff.ToString(),
		};
	}
}
