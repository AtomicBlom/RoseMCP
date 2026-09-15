namespace RoseMcp.Worker;

/// <summary>
/// A checkout's git directory, and what the read barrier and the watcher need of it: whether git is
/// writing the tree right now, and whether a path is inside the git directory rather than out in the
/// working tree.
/// <para>
/// Separate from <see cref="SolutionWatcher"/> because both rules are subtle enough to need tests of
/// their own. A git directory that cannot be found removes the guard that stops a barrier reconciling half
/// of one branch and half of another, and a lock thought held when it is not makes every read wait out the
/// settle timeout.
/// </para>
/// <para>
/// Nothing here says a branch was switched. A switch is its files changing, and the barrier already reads
/// every change it needs -- tracked documents by their stamps, new source files by walking, and build
/// files by their stamps or by their appearing -- so a switch that touches only source is absorbed, and one
/// that moves a project or an import reloads for that reason and no other.
/// </para>
/// </summary>
public sealed class GitDirectory
{
	private const string GitFileMarker = "gitdir:";

	private GitDirectory(string fullPath) => FullPath = Path.GetFullPath(fullPath);

	/// <summary>The absolute path of the directory holding HEAD, the index and the refs.</summary>
	public string FullPath { get; }

	/// <summary>
	/// The git directory covering <paramref name="start"/>, or null outside a repository.
	/// <para>
	/// A linked worktree's <c>.git</c> is a file rather than a directory, naming the real one under the
	/// main repository's <c>worktrees</c> folder. Testing only for a directory walks straight past it
	/// and finds an unrelated repository further up or nothing at all, which leaves every worktree of
	/// a repository unable to answer whether git is mid-operation.
	/// </para>
	/// </summary>
	public static GitDirectory? Find(string start)
	{
		var directory = new DirectoryInfo(start);

		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, ".git");

			if (Directory.Exists(candidate)) return new GitDirectory(candidate);
			if (File.Exists(candidate) && Linked(candidate, directory.FullName) is { } linked)
			{
				return new GitDirectory(linked);
			}

			directory = directory.Parent;
		}

		return null;
	}

	/// <summary>Whether a path is inside the git directory rather than out in the working tree.</summary>
	public bool Contains(string path) =>
		path.StartsWith(FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True while git holds its index lock, which it takes to write the index and the working tree and
	/// gives back when it stops. Reconciling then would read a tree that is half the old branch and half
	/// the new one.
	/// <para>
	/// The lock and nothing else. <c>MERGE_HEAD</c> and <c>REBASE_HEAD</c> describe a state rather than
	/// an operation: they last as long as somebody takes to resolve a conflict, while the tree is stable,
	/// and git can leave <c>REBASE_HEAD</c> behind after the rebase that wrote it has finished. Counting
	/// either makes every read wait out the settle timeout and then reload the whole solution, for as long
	/// as the file exists. A checkout replacing the tree is already said by HEAD being rewritten, and the
	/// stat sweep reconciles whatever a merge or rebase writes.
	/// </para>
	/// </summary>
	public bool OperationInFlight() => File.Exists(Path.Combine(FullPath, "index.lock"));

	/// <summary>
	/// The directory a linked worktree's <c>.git</c> file points at, or null for anything that is not
	/// one. The path it names is usually absolute and is allowed to be relative to the worktree.
	/// </summary>
	private static string? Linked(string gitFile, string worktree)
	{
		string text;

		try
		{
			text = File.ReadAllText(gitFile);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		foreach (var line in text.Split('\n'))
		{
			var trimmed = line.Trim();
			if (!trimmed.StartsWith(GitFileMarker, StringComparison.Ordinal)) continue;

			var target = trimmed[GitFileMarker.Length..].Trim();
			if (target.Length == 0) return null;

			var resolved = Path.GetFullPath(Path.Combine(worktree, target));

			return Directory.Exists(resolved) ? resolved : null;
		}

		return null;
	}
}
