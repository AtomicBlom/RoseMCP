
namespace RoseMcp.UnitTests;

/// <summary>
/// Where the git directory is, what counts as inside it, and when git is writing the tree.
/// <para>
/// The first two decide which events the watcher throws away and whether a worktree can be asked about
/// git at all; the last decides whether a read waits. The lock is the expensive one to get wrong: a marker
/// that outlives its operation makes every read wait out the settle timeout.
/// </para>
/// </summary>
public sealed class GitDirectoryTests
{

	/// <summary>
	/// A sibling whose name merely starts with the git directory's is out in the working tree. Held
	/// by requiring a separator, which a plain prefix test does not.
	/// </summary>
	[Test]
	public void A_name_beginning_with_dot_git_is_not_inside_the_git_directory()
	{
		using var checkout = Checkout.Ordinary();

		checkout.Git.Contains(Path.Combine(checkout.Root, ".gitignore")).ShouldBeFalse();
		checkout.Git.Contains(checkout.InGitDirectory("HEAD")).ShouldBeTrue();
	}

	[Test]
	public void Finds_the_git_directory_of_an_ordinary_checkout()
	{
		using var checkout = Checkout.Ordinary();

		var found = GitDirectory.Find(Path.Combine(checkout.Root, "src"));

		found.ShouldNotBeNull();
		found.FullPath.ShouldBe(Path.Combine(checkout.Root, ".git"));
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

		found.ShouldNotBeNull();
		found.FullPath.ShouldBe(linked);
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

		found.ShouldNotBeNull();
		found.FullPath.ShouldBe(linked);
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
			(GitDirectory.Find(root.FullName)?.FullPath).ShouldNotBe(Path.Combine(root.FullName, ".git"));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// The index lock is the one marker of git writing the tree right now: taken for the write and given
	/// back when it stops, so it is worth a barrier waiting for.
	/// </summary>
	[Test]
	public void The_index_lock_is_an_operation_in_flight()
	{
		using var checkout = Checkout.Ordinary();
		File.WriteAllText(checkout.InGitDirectory("index.lock"), "");

		checkout.Git.OperationInFlight().ShouldBeTrue();
	}

	/// <summary>
	/// A merge or rebase stopped part-way, or finished with its marker left behind. Neither is git writing
	/// anything: the tree is stable while somebody resolves a conflict, and git can leave
	/// <c>REBASE_HEAD</c> after a rebase that finished. Counted as in flight, every read waits out the
	/// settle timeout and then reloads the solution for as long as the file exists.
	/// </summary>
	[Test]
	[Arguments("MERGE_HEAD")]
	[Arguments("REBASE_HEAD")]
	public void A_merge_or_rebase_marker_is_not_an_operation_in_flight(string marker)
	{
		using var checkout = Checkout.Ordinary();
		File.WriteAllText(checkout.InGitDirectory(marker), "464bce08e0dc4806d80c0f1a918aba7fee578338\n");

		checkout.Git.OperationInFlight().ShouldBeFalse($"{marker} is a state git can leave behind, not a write in progress");
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
