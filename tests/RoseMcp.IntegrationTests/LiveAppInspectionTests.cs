using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a session says about itself, and what it says about a stop, read the way an operator view
/// reads it.
/// <para>
/// Every test here drives the plain .NET probe target rather than a XAML app, so none of them takes
/// a lease on a single-instance package and they run alongside the rest of the suite. That is also
/// the interesting case for the self-report: a target that is not a XAML app has to say so, with a
/// reason, rather than leaving a reader to conclude it from an absence.
/// </para>
/// </summary>
public sealed class LiveAppInspectionTests
{
	/// <summary>
	/// The fields an operator view acts on, from a target that is not a XAML app at all. Each of
	/// them is a fact the session lifecycle cannot carry: a Ready session may be running or held, and
	/// one whose host has stopped answering goes on describing itself accurately as of some moment.
	/// </summary>
	[Test]
	public async Task The_self_report_names_the_stack_the_provider_and_the_heartbeat()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			// The report comes from a poll, so the first one is what is being waited for here.
			var summary = await WaitForSummaryAsync(session, described => described.InfoAge is not null, cancellationToken);
			Assert.NotNull(summary);

			Assert.Equal(LiveAppSessionState.Ready, summary!.State);
			Assert.Equal(LiveExecutionState.Running, summary.Execution);
			Assert.Null(summary.Stop);

			// A console app is no XAML framework, and the reason names what was read to decide that.
			Assert.Equal(XamlStack.Unknown, summary.XamlStack);
			Assert.Contains("loaded modules", summary.XamlStackReason);
			Assert.Equal(LiveXamlProvider.None, summary.XamlProvider);

			// The log that explains this session, so a reader has somewhere to go.
			Assert.NotNull(summary.HostLogPath);
			Assert.EndsWith(".log", summary.HostLogPath);

			// The probe throws every 200ms, so a heartbeat this stale means the event stream has died.
			var heartbeat = await WaitForSummaryAsync(session, described => described.LastEventAge is not null, cancellationToken);
			Assert.NotNull(heartbeat);
			Assert.True(
				heartbeat!.LastEventAge < TimeSpan.FromSeconds(30),
				$"the target last spoke {heartbeat.LastEventAge} ago");

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A session's calls are recorded the way a worker's are, which is what gives an operator view
	/// anything to show. The self-report is deliberately not among them: it runs every second, and
	/// recording it would fill the recent list with the poll that reads the recent list.
	/// </summary>
	[Test]
	public async Task A_sessions_calls_are_recorded_as_activities_and_its_own_poll_is_not()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);
			await session.SetBreakpointAsync("DebugProbeTarget.Program.Beat", autoContinueSeconds: 1, condition: null, cancellationToken);

			var summary = session.Describe();
			var recorded = summary.Recent.FirstOrDefault(activity => activity.Operation == ToolNames.LiveAppSetBreakpoint);
			Assert.NotNull(recorded);
			Assert.Equal(ActivityOutcome.Succeeded, recorded!.Outcome);

			// The argument that says which call this was, rather than only which tool.
			Assert.Contains("Program.Beat", recorded.Target);

			var polls = summary.Recent.Concat(summary.Running).Where(activity => activity.Operation == ToolNames.LiveAppInfo);
			Assert.Empty(polls);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A stop's locals carry the names the code declares, read from the module's portable PDB, and
	/// the evaluator resolves a caller who passes one of them back.
	/// <para>
	/// The names are the whole point: a caller told <c>local_0</c> has to count declarations to guess
	/// which variable that is, and the guess is silent when it is wrong. So both halves are asserted,
	/// because reporting a name the evaluator cannot then resolve would be worse than reporting the
	/// slot.
	/// </para>
	/// </summary>
	[Test]
	public async Task Locals_at_a_stop_carry_their_source_names()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProbeTarget();
		try
		{
			var session = await manager.StartAsync(Attach(child.Id), cancellationToken);

			var breakpoint = await session.SetBreakpointAsync(
				"DebugProbeTarget.Program.Inspect", autoContinueSeconds: null, condition: null, cancellationToken);
			Assert.True(breakpoint.Bound, $"breakpoint should bind; detail: {breakpoint.Detail}");

			var stop = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.BreakpointHit && entry.Message.Contains("stopped"),
				cancellationToken);
			Assert.NotNull(stop);
			Assert.NotNull(stop!.Variables);

			// The argument is named from metadata, as it always was.
			var argument = stop.Variables!.FirstOrDefault(variable => variable.Name == "state");
			Assert.NotNull(argument);
			Assert.Equal("argument", argument!.Kind);

			// The local is named from the PDB. Slot names are the fallback, so one appearing here is
			// the reader having found no symbols rather than a different name.
			var local = stop.Variables!.FirstOrDefault(variable => variable.Kind == "local");
			Assert.NotNull(local);
			Assert.Equal("innerCount", local!.Name);
			Assert.Equal("int", local.TypeName);

			// And the name resolves back, which is what makes it worth reporting.
			var evaluated = await session.EvaluateAsync("innerCount", cancellationToken);
			Assert.True(evaluated.Error is null, $"innerCount should evaluate; error: {evaluated.Error}");
			Assert.Equal("int", evaluated.TypeName);

			await session.RemoveBreakpointAsync(breakpoint.Id, cancellationToken);
			Assert.True(await session.ContinueAsync(cancellationToken));
			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
			Assert.False(child.HasExited, "the target is still running after being read");
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	private static LiveAppTarget Attach(int processId) => new()
	{
		Kind = LiveAppTargetKind.AttachProcess,
		ProcessId = processId,
		Description = "probe target",
	};

	/// <summary>
	/// Reads the session's own description until it matches, or ten seconds pass. The report is filled
	/// by a poll on its own timer, so what a caller sees immediately after attaching is a session that
	/// has not been asked yet.
	/// </summary>
	private static async Task<LiveAppSessionSummary?> WaitForSummaryAsync(
		LiveAppSession session,
		Func<LiveAppSessionSummary, bool> match,
		CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

		while (DateTime.UtcNow < deadline)
		{
			var summary = session.Describe();
			if (match(summary)) return summary;

			await Task.Delay(200, cancellationToken);
		}

		return null;
	}
}
