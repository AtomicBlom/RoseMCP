using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// A write's result as the caller is shown it: the diff only where it says something the caller does
/// not already know, and the changed files named up to a cap with a count past it.
/// <para>
/// The diff of an applied write is left off unless it was asked for, because nearly all of it is
/// what the caller sent: a member composed one call ago, read back. What the writer owns is where it
/// landed and what it normalised, and <see cref="WorkspaceMutationResult.ChangedFiles"/> says both in
/// a few characters per file. A preview keeps its diff, because a preview is the diff -- a caller
/// asking what a change would do wants to read it.
/// </para>
/// <para>
/// Both cuts are made here rather than in the worker because the broker needs every path first. A
/// write's files may belong to projects a sibling solution also compiles, and the notice saying so is
/// worked out from <see cref="WorkspaceMutationResult.ChangedFiles"/> in <see cref="WorkspaceManager"/>,
/// after the worker answers; a worker that capped the list would hide a sibling whose shared files fell
/// past the cap. So the worker sends them all, the manager reads them all, and only what leaves the
/// tool is cut. Uncut, a migration's paths and diff are the largest fields in its result, and run past
/// what a client accepts, so the caller gets nothing at all.
/// </para>
/// </summary>
public static class WriteForCaller
{
	/// <summary>How many changed files the caller is shown by name: all of them in an edit of a few files.</summary>
	public const int ChangedFileRows = 20;

	/// <summary>The longest diff a result carries; beyond it the diff is left out, with a notice saying where to read it.</summary>
	public const int DiffCeiling = 16_000;

	/// <summary>
	/// <paramref name="result"/> as the caller is shown it: the diff kept only on a preview or where
	/// <paramref name="includeDiff"/> asked, and only while it is short enough to read; the changed files
	/// cut to <see cref="ChangedFileRows"/>; and a notice for each cut that dropped something.
	/// </summary>
	public static T Shape<T>(T result, bool includeDiff)
		where T : WorkspaceMutationResult
	{
		WorkspaceMutationResult shaped = result;
		var notices = new List<string>(result.Notices);

		var diff = includeDiff || !result.Applied ? result.Diff : null;
		if (string.IsNullOrEmpty(diff)) diff = null;

		var files = result.ChangedFiles;
		var isCut = files.Count > ChangedFileRows;

		if (diff is { Length: > DiffCeiling })
		{
			notices.Add(DiffLeftOut(diff.Length, result.Applied, files, isCut));
			diff = null;
		}

		if (isCut)
		{
			var verb = result.Applied ? "wrote" : "would write";

			notices.Add($"changedFiles names {ChangedFileRows} of the {files.Count} files this {verb}.");
			files = [.. files.Take(ChangedFileRows)];
		}

		var unchanged = ReferenceEquals(diff, result.Diff) && ReferenceEquals(files, result.ChangedFiles);
		if (unchanged) return result;

		return (T)(shaped with { Diff = diff, ChangedFiles = files, Notices = notices });
	}

	/// <summary>
	/// What to say about a diff too long to carry, and where to read it instead -- only somewhere it is.
	/// <para>
	/// An applied write is on disk, so asking again returns nothing and git is where the change is; but a
	/// file the write created is untracked, and git diff leaves it out, so such a file is named to be read
	/// instead. A preview over several files can be asked again over fewer; a preview of one file cannot be
	/// narrowed, so it is told nothing it cannot follow. Where the changed files were cut too, the lines
	/// they give are for the ones named, not for every file the diff covered.
	/// </para>
	/// </summary>
	private static string DiffLeftOut(int length, bool applied, IReadOnlyList<ChangedFile> files, bool isCut)
	{
		var created = files.Where(file => file.Created == true).Select(file => file.FilePath).ToList();
		var them = created.Count == 1 ? "it" : "them";

		string? where;
		if (applied && created.Count == 0)
		{
			where = "the write is done, so git diff shows it";
		}
		else if (applied && created.Count == files.Count)
		{
			where = $"the write is done, and git diff leaves out {Named(created)}, which it created and git does not track, so read {them}";
		}
		else if (applied)
		{
			where = $"the write is done, so git diff shows its changes to files that existed, but not {Named(created)}, which it created "
				+ $"and git does not track, so read {them}";
		}
		else
		{
			where = files.Count > 1 ? "a preview over fewer files returns it" : null;
		}

		var lines = (isCut, files.Count) switch
		{
			(true, _) => $"changedFiles gives the changed lines of the first {ChangedFileRows} files only",
			(_, 1) => "changedFiles gives its changed lines",
			_ => "changedFiles gives each file's changed lines",
		};

		var said = where is null ? lines : $"{where}, and {lines}";

		return $"The diff is {length:N0} characters, past the {DiffCeiling:N0} a result carries, and was left out: {said}.";
	}

	/// <summary>
	/// Up to three paths by name and a count of the rest, so a notice about created files stays one line
	/// however many a migration created.
	/// </summary>
	private static string Named(IReadOnlyList<string> paths) =>
		paths.Count <= 3
			? string.Join(", ", paths)
			: $"{string.Join(", ", paths.Take(3))} and {paths.Count - 3} more";
}
