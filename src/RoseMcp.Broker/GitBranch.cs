namespace RoseMcp.Broker;

/// <summary>
/// The branch checked out in the git checkout holding a directory, read from the checkout's own files.
/// <para>
/// Read rather than asked of git, because it is asked on behalf of an agent session, by a relay that must
/// not hold up the session or outlive it: a <c>HEAD</c> file is a line long, and reading one cannot prompt,
/// hang or leave a child process behind. A worktree's <c>.git</c> is a file naming the directory its
/// <c>HEAD</c> is in, which is followed the same way. Anything unexpected -- no checkout, a file that cannot
/// be read, a <c>HEAD</c> in a shape this does not know -- is no branch rather than an error, since the branch
/// only ever labels something.
/// </para>
/// </summary>
public static class GitBranch
{
	/// <summary>The most of a <c>HEAD</c> or <c>.git</c> file read: either is one short line.</summary>
	private const int MaxRead = 4096;

	/// <summary>How a <c>HEAD</c> on a branch begins.</summary>
	private const string SymbolicHead = "ref: refs/heads/";

	/// <summary>How a worktree's <c>.git</c> file begins.</summary>
	private const string GitDirectoryLine = "gitdir:";

	/// <summary>
	/// The branch checked out in the checkout holding <paramref name="directory"/>; "detached at" and seven
	/// digits of the commit for a detached head; null where there is no checkout or it cannot be read.
	/// </summary>
	public static string? Of(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory)) return null;

		try
		{
			for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
			{
				var dotGit = Path.Combine(current.FullName, ".git");

				if (Directory.Exists(dotGit)) return FromHead(dotGit);
				if (File.Exists(dotGit)) return GitDirectoryOf(dotGit) is { } linked ? FromHead(linked) : null;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}

		return null;
	}

	/// <summary>The branch a <c>HEAD</c> line names: a ref under <c>refs/heads/</c>, or a commit for a detached head.</summary>
	public static string? FromHeadLine(string? line)
	{
		var head = line?.Trim();
		if (string.IsNullOrEmpty(head)) return null;

		if (head.StartsWith(SymbolicHead, StringComparison.Ordinal))
		{
			var branch = head[SymbolicHead.Length..].Trim();
			return branch.Length == 0 ? null : branch;
		}

		var isCommit = head.Length is 40 or 64 && head.All(char.IsAsciiHexDigit);

		return isCommit ? $"detached at {head[..7].ToLowerInvariant()}" : null;
	}

	private static string? FromHead(string gitDirectory)
	{
		var head = Path.Combine(gitDirectory, "HEAD");

		return File.Exists(head) ? FromHeadLine(FirstLine(head)) : null;
	}

	/// <summary>The directory a worktree's <c>.git</c> file points at, relative to the file where it is not absolute.</summary>
	private static string? GitDirectoryOf(string dotGitFile)
	{
		var line = FirstLine(dotGitFile)?.Trim();
		if (line is null || !line.StartsWith(GitDirectoryLine, StringComparison.Ordinal)) return null;

		var target = line[GitDirectoryLine.Length..].Trim();
		if (target.Length == 0) return null;

		var resolved = Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(dotGitFile)!, target);

		return Directory.Exists(resolved) ? resolved : null;
	}

	private static string? FirstLine(string path)
	{
		using var reader = new StreamReader(path);

		var buffer = new char[MaxRead];
		var read = reader.ReadBlock(buffer, 0, buffer.Length);
		var text = new string(buffer, 0, read);
		var end = text.IndexOfAny(['\r', '\n']);

		return end < 0 ? text : text[..end];
	}
}
