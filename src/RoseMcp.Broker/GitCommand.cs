using System.ComponentModel;
using System.Diagnostics;

namespace RoseMcp.Broker;

/// <summary>
/// Runs one git command in a checkout, bounded, and reports what it printed or why it could not.
/// <para>
/// Everything about the child is contained. Its output is redirected, never inherited, because a
/// stdio broker's stdout carries protocol frames and nothing else. Its stdin is closed at once and
/// prompting is turned off, so a command that would ask for credentials fails instead of waiting.
/// And it is killed, with anything it started, when the budget runs out or the caller gives up,
/// because a git that hangs on a network share or a lock would otherwise outlive the request that
/// started it and hold the checkout open.
/// </para>
/// <para>
/// A failure is an answer rather than an exception: git missing, a ref that does not exist and a
/// timeout are all things a person can be told in a sentence. Only the caller's own cancellation
/// throws.
/// </para>
/// </summary>
/// <param name="executable">git, or a stand-in a test can make hang.</param>
public class GitCommand(string executable = "git")
{
	/// <summary>
	/// How long one command gets by default. Counting commits takes milliseconds, so this is only the
	/// ceiling on a hang; a reader waiting on the count is bounded separately, by
	/// <see cref="CheckoutDistanceReader.AnswerWithin"/>.
	/// </summary>
	public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(4);

	/// <summary>Runs <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
	/// <param name="workingDirectory">The checkout to run in.</param>
	/// <param name="arguments">The arguments, each passed as one, so a path with spaces needs no quoting.</param>
	/// <param name="budget">How long it may take before it is killed.</param>
	/// <param name="cancellationToken">The caller giving up, which kills it too, and then throws.</param>
	public virtual async Task<GitOutcome> RunAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		TimeSpan budget,
		CancellationToken cancellationToken)
	{
		var start = new ProcessStartInfo(executable)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		foreach (var argument in arguments) start.ArgumentList.Add(argument);

		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

		using var process = new Process { StartInfo = start };

		try
		{
			process.Start();
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return GitOutcome.Failed($"{executable} could not be started ({exception.Message}); is it installed and on PATH?");
		}

		process.StandardInput.Close();

		// Read alongside the wait rather than after it: a child that fills a pipe nobody is draining
		// blocks on the write and never exits.
		var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(budget);

		try
		{
			await process.WaitForExitAsync(bounded.Token);
		}
		catch (OperationCanceledException)
		{
			Kill(process);
			await DrainAsync(output, error);

			cancellationToken.ThrowIfCancellationRequested();

			return GitOutcome.Failed($"{executable} {string.Join(' ', arguments)} took longer than {budget.TotalSeconds:0} seconds and was stopped.");
		}

		return new GitOutcome
		{
			ExitCode = process.ExitCode,
			Output = await output,
			Error = await error,
		};
	}

	/// <summary>
	/// Ends the child and whatever it started. A process that exited between the timeout and the
	/// kill is the race this tolerates, not a failure.
	/// </summary>
	private static void Kill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);

			// Briefly, so the handle is released with the process rather than after it.
			process.WaitForExit(TimeSpan.FromSeconds(2));
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
		{
		}
	}

	/// <summary>
	/// Lets the readers of a killed child finish before the process is disposed under them, so
	/// neither ends as an unobserved failure. A killed child's pipes close with it, so this is quick;
	/// it is bounded anyway, because a grandchild that escaped the kill could hold one open.
	/// </summary>
	private static async Task DrainAsync(Task<string> output, Task<string> error)
	{
		try
		{
			await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
		{
		}
	}
}
