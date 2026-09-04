using System.Security.Cryptography;
using System.Text;

namespace RoseMcp.Solutions;

/// <summary>
/// A short, stable name for one loaded solution, fit for a caller to quote back.
/// <para>
/// Derived from the path rather than minted per process, because a key that dies with its worker is
/// worse than no key: workers are replaced routinely -- a deploy, a crash, a reload to pick up a
/// rebuilt generator -- and a caller holding a key from before would be told its workspace no longer
/// exists when nothing about the workspace changed.
/// </para>
/// <para>
/// Spelled and hashed both, because the name alone collides. Six worktrees of one repository is the
/// ordinary case, not a corner one, and every one of them holds a solution of the same name. The
/// hash is taken over the path folded the way the filesystem folds case -- see
/// <see cref="PathCasing"/>, because that is a question about the platform and not about the path.
/// </para>
/// </summary>
public static class WorkspaceKey
{
	/// <summary>Enough hash to separate the worktrees of a repository, short enough to read aloud.</summary>
	private const int HashBytes = 4;

	public static string For(string solutionPath)
	{
		var full = Path.GetFullPath(solutionPath);
		var hash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(PathCasing.Fold(full))).AsSpan(0, HashBytes))
			.ToLowerInvariant();

		// The extension is dropped but the casing is kept: unlike a log file name, this is shown to
		// a caller who will compare it against the solution they know by sight.
		var readable = Path.GetFileNameWithoutExtension(full);
		if (readable.Length == 0) readable = "workspace";

		return $"{readable}-{hash}";
	}
}
