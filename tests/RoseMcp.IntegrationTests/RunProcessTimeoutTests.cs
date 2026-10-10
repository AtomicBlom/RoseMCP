using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A tool run with a time limit is killed when it overstays, and the failure says what it was doing.
/// <para>
/// In the integration project because it starts a real child process; it needs nothing else, so it
/// runs in the plain integration job. The registration helper runs its deployment scripts this way
/// under a lock every probe fixture queues on, which is what makes the kill matter: a script left
/// running holds all of them.
/// </para>
/// </summary>
public sealed class RunProcessTimeoutTests
{
	[Test]
	public void A_tool_that_overstays_its_limit_is_killed_and_the_failure_names_what_it_was_doing()
	{
		var elapsed = Stopwatch.StartNew();

		var timeout = Should.Throw<TimeoutException>(() => TestToolchain.RunProcess(
			"powershell",
			"-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"",
			TimeSpan.FromSeconds(3),
			"Sleeping through the timeout test"));

		elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "the limit was three seconds; the wait outlived it");
		timeout.Message.ShouldStartWith("Sleeping through the timeout test did not finish within 3 s");
		timeout.Message.ShouldContain("were killed after");

		var pid = int.Parse(Regex.Match(timeout.Message, @"\(pid (\d+)\)").Groups[1].Value);
		ShouldBeGone(pid);
	}

	[Test]
	public void A_tool_that_finishes_inside_its_limit_returns_its_exit_code_and_output()
	{
		var (exitCode, output) = TestToolchain.RunProcess(
			"powershell",
			"-NoProfile -NonInteractive -Command \"'finished'; exit 7\"",
			TimeSpan.FromMinutes(2),
			"Finishing inside the limit");

		exitCode.ShouldBe(7);
		output.ShouldContain("finished");
	}

	private static void ShouldBeGone(int pid)
	{
		try
		{
			using var survivor = Process.GetProcessById(pid);
			survivor.HasExited.ShouldBeTrue($"powershell (pid {pid}) is still running after its limit");
		}
		catch (ArgumentException)
		{
			// No process has that id, which is the outcome the kill wanted.
		}
	}
}
