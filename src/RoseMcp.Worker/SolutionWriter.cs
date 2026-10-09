using System.Text;

using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Writes solution changes to disk and reports where each file changed, with a unified diff.
/// <para>
/// Each file goes back in the encoding it arrived in, which <see cref="SourceEncoding"/> explains.
/// </para>
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
		var changed = new List<ChangedFile>();
		var diff = new StringBuilder();

		// A multi-targeted project loads once per target framework, so one file on disk is a document in
		// each of them and every edit to it is a change in each. It is still one file and one write; listed
		// per project it reads as the same edit made twice.
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
		{
			// Added documents come first: a split writes the new file before the old one shrinks, so
			// a reader that catches the pair mid-write sees the type twice rather than not at all.
			foreach (var documentId in projectChange.GetAddedDocuments())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var added = after.GetDocument(documentId);
				if (added?.FilePath is not { Length: > 0 } addedPath) continue;

				var path = Rooted(addedPath, added);
				if (!seen.Add(path)) continue;

				var source = await added.GetTextAsync(cancellationToken);
				var created = UnifiedDiff.NewFile(path, source.ToString());

				changed.Add(new ChangedFile { FilePath = path, Lines = created.Lines, Created = true });
				diff.Append(created.Text);

				if (!write) continue;

				noteSelfWrite?.Invoke(path);

				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

				await SourceEncoding.WriteAsync(path, source, cancellationToken);
			}

			foreach (var documentId in projectChange.GetChangedDocuments())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var oldDocument = before.GetDocument(documentId);
				var newDocument = after.GetDocument(documentId);
				if (oldDocument?.FilePath is not { Length: > 0 } changedPath || newDocument is null) continue;

				var path = Rooted(changedPath, oldDocument);
				if (seen.Contains(path)) continue;

				var updated = await newDocument.GetTextAsync(cancellationToken);
				var oldText = (await oldDocument.GetTextAsync(cancellationToken)).ToString();
				var newText = updated.ToString();
				if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

				seen.Add(path);

				var compared = UnifiedDiff.Compare(path, oldText, newText);
				diff.Append(compared.Text);

				// Recorded beside the lines because the diff cannot carry it: a terminator is not line
				// content, so the change this most often makes shows there as nothing at all.
				changed.Add(new ChangedFile
				{
					FilePath = path,
					Lines = compared.Lines,
					Normalised = LineEndings.Changed(oldText, newText) is { } moved
						? $"{moved.Lines} line ending(s) to {moved.To}"
						: null,
				});

				if (!write) continue;

				noteSelfWrite?.Invoke(path);
				await SourceEncoding.WriteAsync(path, updated, cancellationToken);
			}
		}

		return new WriteOutcome
		{
			ChangedFiles = changed,
			Diff = diff.ToString(),
		};
	}

	/// <summary>
	/// The document's path, refusing one that is not rooted.
	/// <para>
	/// A relative <see cref="TextDocument.FilePath"/> resolves against the worker process's current
	/// directory -- the repository root in the ordinary case, since a stdio worker inherits its
	/// client's directory -- so the file lands outside the project while the result names a path
	/// that reads as though it did not. The self-write record goes the same way, filed under a key
	/// the disk barrier will never match again, which turns the damage into a mystery about
	/// staleness rather than a bug anyone can find. Refusing names it at the moment it happens.
	/// </para>
	/// </summary>
	private static string Rooted(string path, Document document)
	{
		if (Path.IsPathRooted(path)) return path;

		throw new InvalidOperationException(
			$"Document '{document.Name}' in project '{document.Project.Name}' has the relative path '{path}'. "
			+ "Writing it would resolve that name against the worker's working directory rather than the "
			+ "project directory, so nothing was written.");
	}
}
