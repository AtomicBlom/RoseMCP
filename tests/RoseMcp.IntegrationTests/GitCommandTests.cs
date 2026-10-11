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
			await using var reader = new CheckoutDistanceReader(build, git, TimeProvider.System);

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
	/// sentence. A shell running a long wait in a child of its own stands in for a git hung on a lock
	/// or a network share, and the child is the grandchild a plain kill would leave behind: it is
	/// asserted gone, not just the shell.
	/// </summary>
	[Test]
	public async Task A_command_past_its_budget_is_stopped_and_said()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var pidFile = Path.Combine(Path.GetTempPath(), $"rose-grandchild-{Guid.NewGuid():N}.pid");
		var (shell, arguments) = LongWait(pidFile);
		var clock = Stopwatch.StartNew();

		try
		{
			// Long enough for a cold PowerShell to start its child and write the id.
			var outcome = await new GitCommand(shell).RunAsync(
				Path.GetTempPath(), arguments, TimeSpan.FromSeconds(8), cancellationToken);

			clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
			outcome.Succeeded.ShouldBeFalse();
			outcome.ExitCode.ShouldBeNull();
			outcome.Error.ShouldContain("took longer than 8 seconds", Case.Sensitive);

			await ShouldBeGoneAsync(ReadPid(pidFile), cancellationToken);
		}
		finally
		{
			File.Delete(pidFile);
		}
	}

	/// <summary>
	/// A caller giving up stops the command too, grandchild included, and hears its own cancellation
	/// rather than a timeout. It gives up only once the grandchild exists, so there is one to find.
	/// </summary>
	[Test]
	public async Task A_caller_giving_up_stops_the_command()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var pidFile = Path.Combine(Path.GetTempPath(), $"rose-grandchild-{Guid.NewGuid():N}.pid");
		var (shell, arguments) = LongWait(pidFile);
		using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		try
		{
			var running = new GitCommand(shell).RunAsync(Path.GetTempPath(), arguments, TimeSpan.FromMinutes(5), giveUp.Token);

			var pid = await WaitForPidAsync(pidFile, cancellationToken);
			await giveUp.CancelAsync();

			await Should.ThrowAsync<OperationCanceledException>(() => running);
			await ShouldBeGoneAsync(pid, cancellationToken);
		}
		finally
		{
			File.Delete(pidFile);
		}
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

	/// <summary>
	/// A wait long enough to outlive any budget here, run as a child of a shell so there is a
	/// grandchild, whose process id the shell writes to <paramref name="pidFile"/>.
	/// </summary>
	private static (string Shell, string[] Arguments) LongWait(string pidFile) => OperatingSystem.IsWindows()
		? ("powershell.exe",
		[
			"-NoProfile",
			"-NonInteractive",
			"-Command",
			"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -NoNewWindow -PassThru; "
				+ $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id; $p.WaitForExit()",
		])
		: ("/bin/sh", ["-c", $"sleep 120 & echo $! > '{pidFile}'; wait"]);

	/// <summary>The grandchild's id, which the shell writes once it has started it.</summary>
	private static async Task<int> WaitForPidAsync(string pidFile, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

		while (DateTime.UtcNow < deadline)
		{
			if (TryReadPid(pidFile) is { } pid) return pid;

			await Task.Delay(100, cancellationToken);
		}

		throw new InvalidOperationException($"The shell never wrote its child's id to {pidFile}.");
	}

	private static int ReadPid(string pidFile) =>
		TryReadPid(pidFile) ?? throw new InvalidOperationException(
			$"The shell had not written its child's id to {pidFile} before it was stopped, so the test proves nothing.");

	private static int? TryReadPid(string pidFile)
	{
		try
		{
			return int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) ? pid : null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// That a process has exited, given a moment: a tree kill signals every process in it, and the
	/// last of them can take a beat to go.
	/// </summary>
	private static async Task ShouldBeGoneAsync(int pid, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

		while (IsRunning(pid))
		{
			if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"The grandchild, pid {pid}, outlived the kill.");

			await Task.Delay(100, cancellationToken);
		}
	}

	private static bool IsRunning(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);

			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

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
