using System.Diagnostics;
using System.Text.Json;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using Xunit.Sdk;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Supervising the worker process: giving up on one that misses its handshake, replacing one on
/// restart, noticing one that crashed without being called, serving a retry from a single
/// replacement, cancelling a call without spoiling the worker, and ending the host when its client
/// closes stdin.
/// <para>
/// All of it is about process lifetime, which is why these cost what they do -- each spawns a real
/// worker and then does something to it that a mock could not be wrong about in the same way.
/// </para>
/// </summary>
public sealed class BrokerWorkerTests
{
	/// <summary>
	/// The handshake budget is read from options rather than left to the SDK.
	/// <para>
	/// Asserted by making it fail, because the defect it guards is a property nothing reads: the
	/// option existed, was documented, and was never passed anywhere, so every worker got the SDK's
	/// 60 seconds and the setting promised a patience the code did not have. A test that only starts
	/// a worker successfully cannot tell a wired option from a dead one -- both pass.
	/// </para>
	/// </summary>
	[Test]
	public async Task A_worker_that_misses_the_handshake_budget_is_given_up_on()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager(workerHandshakeTimeout: TimeSpan.FromMilliseconds(1));

		var failure = await Assert.ThrowsAnyAsync<Exception>(() => manager.GetOrStartAsync(
			WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("timed out", failure.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Test]
	public async Task Restart_replaces_the_worker_process()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var before = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);
		var after = await manager.RestartAsync(WorkspaceHints.From(fixture.SolutionPath), TestContext.Current!.Execution.CancellationToken);

		Assert.NotSame(before, after);
		Assert.Equal(WorkerExitReason.StoppedByBroker, before.ExitReason);
		Assert.True(after.IsAlive);
		Assert.Single(manager.Workers);
	}

	/// <summary>
	/// A cancelled call is cancelled through the broker rather than only abandoned by the caller.
	/// </summary>
	/// <remarks>
	/// Nothing referenced <see cref="CancellableToolCall"/> or cancelled a worker call, so the whole
	/// mechanism was unprotected -- including the ordering it exists for, which is sending the
	/// cancellation before abandoning the wait. <c>McpClient.CallToolAsync</c> honours a token by giving
	/// up locally and never telling the far side, which is what that class was written to replace.
	/// <para>
	/// What this asserts is that the path runs and the outcome is recorded as cancelled, and that the
	/// worker is usable straight afterwards rather than wedged. What it does <em>not</em> assert is the
	/// timing the invariant is really about -- that the worker stops work sooner -- because that needs
	/// an operation slow enough for the difference to exceed the noise, and the fixture solutions are
	/// deliberately small. A timing assertion over them would pass either way.
	/// </para>
	/// </remarks>
	[Test]
	public async Task Cancelling_a_call_cancels_it_at_the_broker_and_leaves_the_worker_usable()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Loaded first, so what gets cancelled below is the analysis rather than the load behind it.
		await manager.CallAsync<WorkspaceStatusReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: true,
			cancellationToken);

		using var cancelling = new CancellationTokenSource();

		var slow = manager.CallAsync<DiagnosticsResult>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.Diagnostics,
			new Dictionary<string, object?> { ["scope"] = "solution", ["includeAnalyzers"] = true },
			retryIfWorkerDied: false,
			cancelling.Token);

		await cancelling.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

		Assert.Contains(
			manager.Activities.Recent(fixture.SolutionPath),
			activity => activity.Operation == ToolNames.Diagnostics && activity.Outcome == ActivityOutcome.Cancelled);

		// And the worker answers the next question, rather than the cancellation having taken it with it.
		var afterwards = await manager.CallAsync<WorkspaceStatusReport>(
			WorkspaceHints.From(fixture.SolutionPath),
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: false,
			cancellationToken);

		Assert.Equal(WorkspaceState.Loaded, afterwards.State);
	}

	/// <summary>
	/// A stdio server exits when its client's stdin closes, and takes its workers with it.
	/// </summary>
	/// <remarks>
	/// "Workers die with the broker" covered the Roslyn workers and nothing covered the process
	/// holding them, and six servers were once seen accumulating over one session, one per reconnect.
	/// <para>
	/// This is a regression test rather than the fix for that: the behaviour it asserts already held
	/// when it was written, in every shape that could be arranged -- idle, after a handshake and a
	/// tool listing, mid-call, relaying to a tray, and relaying to a tray that had been killed. What
	/// is left of the report is a client that never closes stdin at all, and no rule inside this
	/// process reaches that.
	/// </para>
	/// <para>
	/// Mid-call is the shape asserted, because it is the one with something to go wrong: the call has
	/// a worker loading a solution behind it, so exiting means ending work in flight rather than
	/// noticing an idle stream. The wait is generous for the same reason -- what is under test is
	/// that it ends, not how quickly.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_stdio_server_exits_when_its_client_closes_stdin()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		using var server = RoseServerProcess.Start();

		await server.InitializeAsync(cancellationToken);

		var workersBefore = WorkerProcessIds();

		// Not awaited: the point is to close stdin while this is still running.
		var call = server.CallToolAsync(
			ToolNames.WorkspaceStatus,
			$$"""{"workspace":{{JsonSerializer.Serialize(fixture.SolutionPath)}}}""",
			cancellationToken);

		var worker = await WaitForNewWorkerAsync(workersBefore, cancellationToken);

		server.CloseStandardInput();

		Assert.True(
			await server.WaitForExitAsync(TimeSpan.FromMinutes(2), cancellationToken),
			$"the server (pid {server.Id}) was still running two minutes after its client's stdin closed");

		// And the worker goes with it, which is the invariant this extends rather than replaces.
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (WorkerProcessIds().Contains(worker) && DateTime.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		}

		Assert.DoesNotContain(worker, WorkerProcessIds());

		// Whether the in-flight call was answered is a race, and asserting either way is wrong. The
		// server finishes work already running before it exits -- measured at fourteen seconds for a
		// call whose solution was still loading -- so the reply is written if stdout is still being
		// read and lost if it is not. Awaited rather than abandoned so the outcome is observed and
		// neither outcome fails: what this test claims is that the process ends and the worker goes
		// with it, both asserted above.
		//
		// The first version asserted that no answer arrives. It passed five runs out of six and then
		// failed on "No exception was thrown", which is the assertion being wrong rather than the
		// behaviour changing.
		try
		{
			using var answered = await call;
			Assert.True(
				answered.RootElement.TryGetProperty("result", out _) || answered.RootElement.TryGetProperty("error", out _),
				"a reply that arrives at all has to be a JSON-RPC result or error");
		}
		catch (Exception exception) when (exception is not TrueException)
		{
			// The other legitimate outcome: the stream went before the answer did.
		}
	}

	/// <summary>
	/// A worker that dies is noticed when it dies, without anyone calling it.
	/// </summary>
	/// <remarks>
	/// Nothing observed exit: the exit reason flipped inside the send path, so a worker that had
	/// crashed while idle went on describing itself as alive until somebody asked it something. The
	/// tray polls exactly this description every couple of seconds, so it showed a dead worker as
	/// loaded for as long as nobody used it -- which is the situation someone opens that window in.
	/// <para>
	/// Waiting for the workspace to go quiet first is what makes this discriminate, and the first
	/// draft did not: killing a worker while its load was still finishing meant the heap refresh
	/// that follows every call met the dead process and flipped the reason anyway, so the test passed
	/// against the un-fixed broker. Nothing polls a worker on its own once the load is done, so a
	/// reason that changes after that changed because the exit was observed.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_crashed_worker_is_noticed_without_being_called()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var worker = await manager.GetOrStartAsync(WorkspaceHints.From(fixture.SolutionPath), cancellationToken);

		Assert.True(worker.IsAlive, $"the worker should be alive; exit reason was '{worker.ExitReason}'");
		Assert.NotNull(worker.ProcessId);

		// The load, and then the heap read that follows it. Both are calls, and a call notices a dead
		// worker by itself -- which is the behaviour this test has to run after rather than alongside.
		await WaitForLoadAsync(worker, cancellationToken);
		await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

		Assert.True(worker.IsAlive, "the worker should still be alive with nothing having been asked of it");

		using (var process = Process.GetProcessById(worker.ProcessId!.Value))
		{
			process.Kill(entireProcessTree: true);
		}

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		while (worker.IsAlive && DateTime.UtcNow < deadline)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		Assert.False(worker.IsAlive, "a worker killed from outside should be reported dead without being called");
		Assert.Equal(WorkerExitReason.Crashed, worker.ExitReason);

		// And the description the tray reads agrees, which is the thing that was wrong.
		var described = Assert.Single(manager.Describe());

		Assert.False(described.Alive, "the description a tray polls should agree that the worker is gone");
		Assert.Equal(WorkspaceState.Faulted, described.State);
	}

	/// <summary>
	/// Waits for a worker's initial load to finish, which is what makes it safe to say that nothing
	/// is about to call it.
	/// </summary>
	private static async Task WaitForLoadAsync(WorkspaceWorker worker, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

		while (DateTime.UtcNow < deadline)
		{
			if (worker.LoadDuration is not null || !worker.IsAlive) return;

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		throw new InvalidOperationException($"The worker for {worker.SolutionPath} never finished loading.");
	}

	/// <summary>
	/// The retry after a worker dies replaces the dead instance rather than closing whatever is
	/// registered for the path.
	/// </summary>
	/// <remarks>
	/// The retry called the restart path, which removes and closes whatever is registered whether or
	/// not it is the instance that just died. Two callers on one dead worker and the second closes
	/// the replacement the first is already loading a solution into, mid-load. Replacing only a dead
	/// instance is what <c>GetOrStart</c> already does.
	/// <para>
	/// This is a characterisation test and it passes against the un-fixed broker, which was checked
	/// rather than assumed. With one caller the two paths are indistinguishable: both end with one
	/// live replacement serving the call. The difference needs two callers arriving on the same dead
	/// worker inside the same window, which is not something a test can arrange reliably -- one that
	/// tried would be asserting a timing and would fail for reasons that are not this bug. What it
	/// does hold is the outcome nobody should be able to break quietly: one worker registered, not a
	/// closed one beside a live one, and the retried call answered.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_call_retried_after_a_worker_dies_is_served_by_one_replacement()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var manager = CreateManager();

		var hints = WorkspaceHints.From(fixture.SolutionPath);
		var original = await manager.GetOrStartAsync(hints, cancellationToken);

		using (var process = Process.GetProcessById(original.ProcessId!.Value))
		{
			process.Kill(entireProcessTree: true);
		}

		var status = await manager.CallAsync<WorkspaceStatusReport>(
			hints,
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?>(),
			retryIfWorkerDied: true,
			cancellationToken);

		Assert.Equal(WorkspaceState.Loaded, status.State);
		Assert.NotEmpty(status.Projects);

		var replacement = Assert.Single(manager.Workers);

		Assert.NotEqual(original.ProcessId, replacement.ProcessId);
		Assert.Equal(fixture.SolutionPath, replacement.SolutionPath);
		Assert.True(replacement.IsAlive, $"the replacement should be alive; exit reason was '{replacement.ExitReason}'");
	}

	/// <summary>The worker this server started, waited for rather than assumed to exist already.</summary>
	private static async Task<int> WaitForNewWorkerAsync(IReadOnlyCollection<int> before, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);

		while (DateTime.UtcNow < deadline)
		{
			var started = WorkerProcessIds().Except(before).ToList();
			if (started.Count > 0) return started[0];

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		throw new InvalidOperationException("The server never started a worker.");
	}

	/// <summary>
	/// Every worker on the machine, because a test cannot ask a server it is deliberately not
	/// talking to. Compared as a difference rather than a count, so another session's workers -- a
	/// tray's, most often -- are not mistaken for this one's.
	/// </summary>
	private static IReadOnlyList<int> WorkerProcessIds()
	{
		var running = Process.GetProcessesByName("RoseMcp.Worker");
		var ids = running.Select(process => process.Id).ToList();
		foreach (var process in running) process.Dispose();

		return ids;
	}
}
