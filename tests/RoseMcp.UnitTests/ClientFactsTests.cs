using RoseMcp.Broker;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The facts a relay gathers about its session that come from the machine rather than the client: the branch
/// its folder is on, read from the checkout's own files, and the process that started it.
/// </summary>
public sealed class ClientFactsTests
{
	[Test]
	public void A_head_names_its_branch_or_the_commit_it_is_detached_at()
	{
		GitBranch.FromHeadLine("ref: refs/heads/orchid/rosemcp-458\n").ShouldBe("orchid/rosemcp-458");
		GitBranch.FromHeadLine(new string('A', 40)).ShouldBe("detached at aaaaaaa");
		GitBranch.FromHeadLine("ref: refs/remotes/origin/main").ShouldBeNull("only a local branch is checked out");
		GitBranch.FromHeadLine("garbage").ShouldBeNull();
		GitBranch.FromHeadLine(null).ShouldBeNull();
	}

	[Test]
	public void The_branch_is_read_from_the_checkout_holding_a_folder()
	{
		using var checkout = new TemporaryDirectory();
		Directory.CreateDirectory(Path.Combine(checkout.Path, ".git"));
		File.WriteAllText(Path.Combine(checkout.Path, ".git", "HEAD"), "ref: refs/heads/main\n");

		var nested = Directory.CreateDirectory(Path.Combine(checkout.Path, "src", "Shop")).FullName;

		GitBranch.Of(nested).ShouldBe("main", "the checkout is found from any folder inside it");
	}

	/// <summary>A worktree's <c>.git</c> is a file naming the directory its own <c>HEAD</c> is in.</summary>
	[Test]
	public void A_worktree_is_followed_to_its_own_head()
	{
		using var root = new TemporaryDirectory();
		var linked = Directory.CreateDirectory(Path.Combine(root.Path, "main", ".git", "worktrees", "card")).FullName;
		File.WriteAllText(Path.Combine(linked, "HEAD"), "ref: refs/heads/orchid/card\n");

		var worktree = Directory.CreateDirectory(Path.Combine(root.Path, "card")).FullName;
		File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/card\n");

		GitBranch.Of(worktree).ShouldBe("orchid/card");
	}

	[Test]
	public void A_folder_outside_any_checkout_has_no_branch()
	{
		GitBranch.Of(Path.Combine(Path.GetTempPath(), "rose-no-such-folder", Guid.NewGuid().ToString("N"))).ShouldBeNull();
		GitBranch.Of(null).ShouldBeNull();
		GitBranch.Of("   ").ShouldBeNull();
	}

	[Test]
	public void A_file_root_is_kept_as_a_local_path()
	{
		var folder = Path.Combine(Path.GetTempPath(), "rose roots", "Shop");

		ClientIntroduction.PathOf(new Uri(folder).AbsoluteUri).ShouldBe(folder);
		ClientIntroduction.PathOf("https://example.com/repo").ShouldBe("https://example.com/repo");
	}

	/// <summary>The relay's client is the process that started it, found where the platform says; shells are walked past.</summary>
	[Test]
	public void The_process_that_started_this_one_is_found()
	{
		ClientProcess.IsShell("cmd").ShouldBeTrue();
		ClientProcess.IsShell("bash").ShouldBeTrue();
		ClientProcess.IsShell("claude").ShouldBeFalse();

		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

		var parent = ClientProcess.ParentOf(Environment.ProcessId);
		parent.ShouldNotBeNull();
		parent.ShouldNotBe(Environment.ProcessId);

		ClientProcess.Find().ShouldNotBeNull();
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		public string Path { get; } = Directory.CreateDirectory(
			System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rose-branch", Guid.NewGuid().ToString("N"))).FullName;

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path, recursive: true);
			}
			catch (IOException)
			{
				// Left for the temp folder's own clean-up.
			}
		}
	}
}
