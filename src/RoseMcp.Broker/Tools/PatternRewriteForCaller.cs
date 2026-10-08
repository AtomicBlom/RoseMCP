using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// A structural rewrite's result as the caller is shown it: the changed files named up to a cap, with
/// the whole number in <see cref="PatternRewriteResult.FilesChanged"/> and a notice past it.
/// <para>
/// Here rather than in the worker because the broker needs every path first. A rewrite's files may
/// belong to projects a sibling solution also compiles, and the notice saying so is worked out from
/// <see cref="WorkspaceMutationResult.ChangedFiles"/> in <see cref="WorkspaceManager"/>, after the
/// worker answers; a worker that capped the list would hide a sibling whose shared files fell past the
/// cap. So the worker sends them all, the manager reads them all, and only what leaves the tool is cut.
/// </para>
/// <para>
/// Only this tool, because only this one reaches thousands of files in a call. At the scale of a
/// migration the paths were the largest field in a result the client then refused, and they repeat
/// what <see cref="PatternRewriteResult.Files"/> and <see cref="PatternRewriteResult.FilesChanged"/>
/// already say. Every other write names the handful of files it touched, and keeps naming them all.
/// </para>
/// </summary>
public static class PatternRewriteForCaller
{
	/// <summary>How many changed files the caller is shown by name: all of them in a rewrite of a few files.</summary>
	public const int ChangedFileRows = 20;

	/// <summary><paramref name="result"/> with its changed files cut to <see cref="ChangedFileRows"/>, saying so when they were.</summary>
	public static PatternRewriteResult Narrow(PatternRewriteResult result)
	{
		var total = result.ChangedFiles.Count;

		if (total <= ChangedFileRows) return result;

		var verb = result.Applied ? "wrote" : "would write";

		return result with
		{
			ChangedFiles = [.. result.ChangedFiles.Take(ChangedFileRows)],
			Notices =
			[
				.. result.Notices,
				$"changedFiles names {ChangedFileRows} of the {total} files this {verb}; filesChanged is the whole number, "
					+ "and files lists the ones with something to look at.",
			],
		};
	}
}
