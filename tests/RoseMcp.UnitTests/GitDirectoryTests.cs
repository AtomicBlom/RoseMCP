namespace RoseMcp.UnitTests;

/// <summary>
/// Which git writes mean the working tree was replaced, and where the git directory is.
/// <para>
/// Both rules decide whether a call pays a full design-time build of every project, so the failure
/// they guard against is not a wrong answer but a solution that reloads itself for no reason. An
/// idle, untouched checkout was doing exactly that, because a background fetch writes files named
/// HEAD and the rule matched on the file name.
/// </para>
/// </summary>
public sealed class GitDirectoryTests
{
	/// <summary>
	/// The two files a background fetch writes. Neither touches a line of source, and matching by
	/// file name treats both as a checkout -- which is what made a repository nobody was editing
	/// reload every time it was asked a question.
	/// </summary>
	[Test]
	[Arguments("refs/remotes/origin/HEAD")]
	[Arguments("logs/refs/remotes/origin/HEAD")]
	[Arguments("logs/HEAD")]
	public void A_fetch_writing_a_ref_named_head_is_not_a_tree_replacement(string relative)
	{
		using var checkout = Checkout.Ordinary();

		Assert.False(
			checkout.Git.IsTreeReplaced(checkout.InGitDirectory(relative)),
			$"{relative} is written by a fetch, which changes no source");
	}

	/// <summary>
	/// The index is rewritten by a plain <c>git status</c>, to refresh its stat cache. Every IDE git
	/// integration runs that continuously and runs it in reaction to file writes, so counting it
	/// would let an agent editing C# drive its own reloads.
	/// </summary>
	[Test]
	public void A_status_refreshing_the_index_is_not_a_tree_replacement()
	{
		using var checkout = Checkout.Ordinary();

		Assert.False(checkout.Git.IsTreeReplaced(checkout.InGitDirectory("index")));
	}

	/// <summary>
	/// The branch pointer itself, which is the one file a checkout rewrites and a commit does not.
	/// </summary>
	[Test]
	public void The_branch_pointer_is_a_tree_replacement()
	{
		using var checkout = Checkout.Ordinary();

		Assert.True(checkout.Git.IsTreeReplaced(checkout.InGitDirectory("HEAD")));
	}

	/// <summary>
	/// A sibling whose name merely starts with the git directory's is out in the working tree. Held
	/// by requiring a separator, which a plain prefix test does not.
	/// </summary>
	[Test]
	public void A_name_beginning_with_dot_git_is_not_inside_the_git_directory()
	{
		using var checkout = Checkout.Ordinary();

		Assert.False(checkout.Git.Contains(Path.Combine(checkout.Root, ".gitignore")));
		Assert.True(checkout.Git.Contains(checkout.InGitDirectory("HEAD")));
	}

	[Test]
	public void Finds_the_git_directory_of_an_ordinary_checkout()
	{
		using var checkout = Checkout.Ordinary();

		var found = GitDirectory.Find(Path.Combine(checkout.Root, "src"));

		Assert.NotNull(found);
		Assert.Equal(Path.Combine(checkout.Root, ".git"), found.FullPath);
	}

	/// <summary>
	/// A linked worktree's <c>.git</c> is a file naming the real directory under the main
	/// repository's <c>worktrees</c> folder. Testing only for a directory walks straight past it, so
	/// every worktree of a repository loses the guard that stops a barrier reconciling half of one
	/// branch and half of another -- and several worktrees of one repository is the ordinary case.
	/// </summary>
	[Test]
	public void Finds_the_git_directory_a_linked_worktree_points_at()
	{
		using var checkout = Checkout.WithWorktree(out var worktree, out var linked);

		var found = GitDirectory.Find(worktree);

		Assert.NotNull(found);
		Assert.Equal(linked, found.FullPath);
	}

	/// <summary>
	/// A relative <c>gitdir:</c> is resolved against the worktree, since git is entitled to write
	/// one and a path resolved against the process's own directory would point anywhere at all.
	/// </summary>
	[Test]
	public void Reads_a_relative_gitdir_against_the_worktree()
	{
		using var checkout = Checkout.Ordinary();

		var linked = Path.Combine(checkout.Root, ".git", "worktrees", "wt");
		Directory.CreateDirectory(linked);

		var worktree = Path.Combine(checkout.Root, "wt");
		Directory.CreateDirectory(worktree);
		File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/wt\n");

		var found = GitDirectory.Find(worktree);

		Assert.NotNull(found);
		Assert.Equal(linked, found.FullPath);
	}

	/// <summary>
	/// A directory with no <c>.git</c> of its own does not get one invented for it. Asserted against
	/// this directory rather than as a flat null, because a temporary folder can sit under a
	/// repository on somebody's machine and the walk is supposed to find that one.
	/// </summary>
	[Test]
	public void Claims_no_git_directory_where_there_is_none()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-nogit-");

		try
		{
			Assert.NotEqual(Path.Combine(root.FullName, ".git"), GitDirectory.Find(root.FullName)?.FullPath);
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>A staged directory layout that goes away with the test.</summary>
	private sealed class Checkout : IDisposable
	{
		private Checkout(string root) => Root = root;

		public string Root { get; }

		public GitDirectory Git => GitDirectory.Find(Root)!;

		public static Checkout Ordinary()
		{
			var root = Directory.CreateTempSubdirectory("rosemcp-git-").FullName;
			Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "remotes", "origin"));
			Directory.CreateDirectory(Path.Combine(root, ".git", "logs", "refs", "remotes", "origin"));
			Directory.CreateDirectory(Path.Combine(root, "src"));

			return new Checkout(root);
		}

		public static Checkout WithWorktree(out string worktree, out string linked)
		{
			var checkout = Ordinary();

			linked = Path.Combine(checkout.Root, ".git", "worktrees", "wt");
			Directory.CreateDirectory(linked);

			worktree = Path.Combine(checkout.Root, "wt");
			Directory.CreateDirectory(worktree);
			File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {linked}\n");

			return checkout;
		}

		public string InGitDirectory(string relative) =>
			Path.Combine(Root, ".git", relative.Replace('/', Path.DirectorySeparatorChar));

		public void Dispose() => Directory.Delete(Root, recursive: true);
	}
}
