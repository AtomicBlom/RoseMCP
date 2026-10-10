using System.Diagnostics;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The git process behind a checkout's distance, run for real: counted in a repository made for the
/// test, and stopped when it outlives its budget or its caller.
/// <para>
/// An integration test because it starts processes. The bookkeeping around them -- the cache, the
/// shared count, a failure as a sentence -- is in <c>CheckoutDistanceReaderTests</c>, against a git
/// that answers without one.
/// </para>
/// </summary>
public sealed class GitCommandTests
{
	/// <summary>
	/// The count a person reads: three commits made after the build's commit are three past it on
	/// <c>HEAD</c>, and a checkout with no <c>origin</c> says so in git's words rather than failing.
	/// </summary>
	[Test]
	public async Task Counts_the_commits_a_checkout_has_made_since_the_build()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var git = new GitCommand();
		await RequireGitAsync(git, cancellationToken);

		var checkout = Directory.CreateTempSubdirectory("rose-checkout-");

		try
		{
			await RunAsync(git, checkout.FullName, cancellationToken, "init", "--quiet");
			await CommitAsync(git, checkout.FullName, "the build", cancellationToken);
			var built = (await RunAsync(git, checkout.FullName, cancellationToken, "rev-parse", "HEAD")).Trim();

			for (var i = 0; i < 3; i++) await CommitAsync(git, checkout.FullName, $"after {i}", cancellationToken);

			var build = new BuildIdentity { Version = "1.0.0", Commit = built, Dirty = false, Checkout = checkout.FullName };
			using var reader = new CheckoutDistanceReader(build, git, TimeProvider.System);

			var distance = await reader.ReadAsync(cancellationToken);

			distance.ShouldNotBeNull();
			distance!.Head.Count.ShouldBe(3, distance.Head.Unknown);
			distance.OriginMain.Count.ShouldBeNull();
			distance.OriginMain.Unknown.ShouldNotBeNull();
			distance.OriginMain.Unknown!.ShouldContain("origin/main", Case.Sensitive);
		}
		finally
		{
			DeleteRepository(checkout);
		}
	}

	/// <summary>
	/// A command that outlives its budget is killed, with whatever it started, and reported as a
	/// sentence. A shell running a long wait stands in for a git hung on a lock or a network share:
	/// the shell's child is the grandchild a plain kill would leave behind.
	/// </summary>
	[Test]
	public async Task A_command_past_its_budget_is_stopped_and_said()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var (shell, arguments) = LongWait();
		var clock = Stopwatch.StartNew();

		var outcome = await new GitCommand(shell).RunAsync(
			Path.GetTempPath(), arguments, TimeSpan.FromSeconds(1), cancellationToken);

		clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
		outcome.Succeeded.ShouldBeFalse();
		outcome.ExitCode.ShouldBeNull();
		outcome.Error.ShouldContain("took longer than 1 seconds", Case.Sensitive);
	}

	/// <summary>A caller giving up stops the command too, and hears its own cancellation rather than a timeout.</summary>
	[Test]
	public async Task A_caller_giving_up_stops_the_command()
	{
		var (shell, arguments) = LongWait();
		using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
		giveUp.CancelAfter(TimeSpan.FromMilliseconds(500));
		var clock = Stopwatch.StartNew();

		await Should.ThrowAsync<OperationCanceledException>(() => new GitCommand(shell).RunAsync(
			Path.GetTempPath(), arguments, TimeSpan.FromMinutes(5), giveUp.Token));

		clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
	}

	/// <summary>A git that is not there is an answer saying so, not an exception.</summary>
	[Test]
	public async Task A_missing_git_is_said()
	{
		var outcome = await new GitCommand("rose-no-such-git").RunAsync(
			Path.GetTempPath(), ["status"], TimeSpan.FromSeconds(5), TestContext.Current!.Execution.CancellationToken);

		outcome.Succeeded.ShouldBeFalse();
		outcome.Error.ShouldContain("rose-no-such-git could not be started", Case.Sensitive);
	}

	/// <summary>A wait long enough to outlive any budget here, through a shell so there is a grandchild.</summary>
	private static (string Shell, string[] Arguments) LongWait() => OperatingSystem.IsWindows()
		? ("cmd.exe", ["/c", "ping -n 60 127.0.0.1 >nul"])
		: ("/bin/sh", ["-c", "sleep 60"]);

	private static async Task RequireGitAsync(GitCommand git, CancellationToken cancellationToken)
	{
		var version = await git.RunAsync(Path.GetTempPath(), ["--version"], TimeSpan.FromSeconds(10), cancellationToken);

		if (!version.Succeeded) MachineLimit.Reached($"git is not available: {version.Error}");
	}

	private static Task CommitAsync(GitCommand git, string checkout, string message, CancellationToken cancellationToken) =>
		RunAsync(
			git,
			checkout,
			cancellationToken,
			"-c",
			"user.name=Rose Tests",
			"-c",
			"user.email=tests@rosemcp.invalid",
			"-c",
			"commit.gpgsign=false",
			"commit",
			"--allow-empty",
			"--quiet",
			"-m",
			message);

	private static async Task<string> RunAsync(GitCommand git, string checkout, CancellationToken cancellationToken, params string[] arguments)
	{
		var outcome = await git.RunAsync(checkout, arguments, TimeSpan.FromSeconds(30), cancellationToken);

		outcome.Succeeded.ShouldBeTrue($"git {string.Join(' ', arguments)}: {outcome.Error}");

		return outcome.Output;
	}

	/// <summary>A repository's object files are read-only on Windows, which a plain recursive delete refuses.</summary>
	private static void DeleteRepository(DirectoryInfo checkout)
	{
		foreach (var file in checkout.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;

		checkout.Delete(recursive: true);
	}
}
