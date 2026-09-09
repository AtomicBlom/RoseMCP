namespace RoseMcp.Worker;

/// <summary>
/// A checkout's git directory, and the two questions the read barrier asks of it.
/// <para>
/// Separate from <see cref="SolutionWatcher"/> because both rules are subtle enough to need tests of
/// their own, and because getting either wrong is expensive rather than merely wrong. A false tree
/// replacement reloads every project in the solution, and a git directory that cannot be found
/// removes the guard that stops a barrier reconciling half of one branch and half of another.
/// </para>
/// </summary>
public sealed class GitDirectory
{
	private const string GitFileMarker = "gitdir:";

	private readonly string _headPath;

	private GitDirectory(string fullPath)
	{
		FullPath = Path.GetFullPath(fullPath);
		_headPath = Path.Combine(FullPath, "HEAD");
	}

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
	/// Whether writing this path says the working tree is being replaced wholesale, which no snapshot
	/// can represent and only a reload can absorb.
	/// <para>
	/// HEAD alone, matched as a whole path. Per git operation: a checkout rewrites HEAD, a commit does
	/// not, and a plain <c>git status</c> rewrites only the index -- to refresh its stat cache, which
	/// says nothing about the working tree. Every IDE git integration runs that continuously and runs
	/// it in reaction to file writes, so treating the index as a marker lets an agent editing C# drive
	/// its own reloads.
	/// </para>
	/// <para>
	/// Matching by file name instead fires on <c>refs/remotes/origin/HEAD</c> and
	/// <c>logs/refs/remotes/origin/HEAD</c>, which a background fetch writes without touching a line of
	/// source. Nothing wider is needed: a project file added, removed or edited by any git operation is
	/// caught by the structural sweep, and file contents by the stat sweep.
	/// </para>
	/// </summary>
	public bool IsTreeReplaced(string path) =>
		string.Equals(Path.GetFullPath(path), _headPath, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// True while git holds its index lock, or a merge or rebase is part-way through. Reconciling then
	/// would read a tree that is half the old branch and half the new one.
	/// </summary>
	public bool OperationInFlight() =>
		File.Exists(Path.Combine(FullPath, "index.lock"))
			|| File.Exists(Path.Combine(FullPath, "MERGE_HEAD"))
			|| File.Exists(Path.Combine(FullPath, "REBASE_HEAD"));

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
