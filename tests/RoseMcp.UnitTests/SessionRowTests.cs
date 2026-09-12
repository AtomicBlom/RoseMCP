using RoseMcp.Contracts;
using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// A debug session as a window shows it.
/// <para>
/// Most of what is asserted here is wording, and it is worth asserting because these are the
/// sentences a reader acts on. Two matter more than the rest: whether a XAML surface is offered at
/// all, which is a fact about the target rather than a feature flag, and what a held target says --
/// an app frozen by a debugger stays frozen until somebody or something lets it go, so the row has
/// to make that obvious.
/// </para>
/// </summary>
public sealed class SessionRowTests
{
	private static LiveAppSessionSummary Session(
		LiveAppSessionState state = LiveAppSessionState.Ready,
		LiveStop? stop = null,
		XamlStack stack = XamlStack.Unknown,
		string reason = "it has loaded no XAML framework",
		LiveXamlProvider provider = LiveXamlProvider.None,
		TimeSpan? lastEventAge = null) => new()
		{
			SessionId = "session-abcd1234",
			TargetDescription = "Widget.exe (launched)",
			Architecture = TargetArchitecture.X64,
			State = state,
			HostProcessId = 4242,
			TargetProcessId = 9191,
			StartedUtc = DateTime.UtcNow.AddMinutes(-3),
			Uptime = TimeSpan.FromMinutes(3),
			Execution = stop?.State ?? LiveExecutionState.Running,
			Stop = stop,
			XamlStack = stack,
			XamlStackReason = reason,
			XamlProvider = provider,
			LastEventAge = lastEventAge,
		};

	private static LiveStop Stop(
		LiveExecutionState state = LiveExecutionState.StoppedAtBreakpoint,
		string? breakpointId = "bp-3",
		LiveStopResume resume = LiveStopResume.AutoContinue,
		int? threadId = 12) => new()
		{
			State = state,
			ThreadId = threadId,
			BreakpointId = breakpointId,
			EventSequence = 57,
			StoppedAtUtc = DateTime.UtcNow,
			Resume = resume,
			ResumeDeadlineUtc = DateTime.UtcNow.AddSeconds(30),
		};

	[Test]
	public void Reads_a_running_session_as_running()
	{
		var row = new SessionRow(Session());

		Assert.Equal("Running", row.StateLabel);
		Assert.True(row.IsHealthy);
		Assert.False(row.IsStopped);
		Assert.Equal(string.Empty, row.ExecutionLabel);
		Assert.Equal(string.Empty, row.ResumeLabel);
	}

	/// <summary>
	/// A held target is Caution rather than Healthy. It is not a failure, but it is a state somebody
	/// has to end, and a card that looks the same as a running one hides the one thing about it that
	/// needs acting on.
	/// </summary>
	[Test]
	public void Reads_a_stopped_session_as_needing_attention()
	{
		var row = new SessionRow(Session(stop: Stop()));

		Assert.Equal("Stopped", row.StateLabel);
		Assert.True(row.IsCaution);
		Assert.True(row.IsStopped);
		Assert.Equal(57, row.StopSequence);
		Assert.Equal("breakpoint bp-3 on thread 12", row.ExecutionLabel);
	}

	/// <summary>A step is a stop with no breakpoint, and says so rather than leaving a gap.</summary>
	[Test]
	public void Names_a_step_rather_than_a_missing_breakpoint()
	{
		var row = new SessionRow(Session(stop: Stop(LiveExecutionState.StoppedAtStep, breakpointId: null)));

		Assert.Equal("a step on thread 12", row.ExecutionLabel);
	}

	[Test]
	[Arguments(LiveAppSessionState.Starting, "Starting")]
	[Arguments(LiveAppSessionState.Faulted, "Faulted")]
	[Arguments(LiveAppSessionState.Ended, "Ended")]
	public void Reads_each_lifecycle_state(LiveAppSessionState state, string expected) =>
		Assert.Equal(expected, new SessionRow(Session(state)).StateLabel);

	/// <summary>
	/// The two frameworks with a diagnostics tap get a surface; the others are told why they do not.
	/// WPF is the interesting case: it has a live visual tree, and its diagnostics are managed rather
	/// than a COM tap, so a reader who can see a WPF window on screen needs the reason rather than a
	/// blank.
	/// </summary>
	[Test]
	[Arguments(XamlStack.Uwp, true)]
	[Arguments(XamlStack.WinUi, true)]
	[Arguments(XamlStack.Wpf, false)]
	[Arguments(XamlStack.Unknown, false)]
	public void Offers_a_visual_tree_only_where_there_is_a_tap(XamlStack stack, bool expected) =>
		Assert.Equal(expected, new SessionRow(Session(stack: stack)).HasXaml);

	[Test]
	public void Names_the_framework_and_whether_a_read_is_cheap()
	{
		var resident = new SessionRow(Session(stack: XamlStack.WinUi, provider: LiveXamlProvider.Resident));
		Assert.Contains("WinUI 3", resident.XamlFact);
		Assert.Contains("provider resident", resident.XamlFact);

		var cold = new SessionRow(Session(stack: XamlStack.Uwp));
		Assert.Contains("UWP", cold.XamlFact);
		Assert.Contains("not injected yet", cold.XamlFact);

		// A provider that has gone is a channel failing quietly, and the next read pays an injection.
		var lost = new SessionRow(Session(stack: XamlStack.Uwp, provider: LiveXamlProvider.Lost));
		Assert.Contains("injects again", lost.XamlFact);
	}

	/// <summary>
	/// A target with no tap carries the reason its stack was decided, because the alternative is a
	/// reader concluding the tool is broken.
	/// </summary>
	[Test]
	public void Says_why_a_target_has_no_visual_tree()
	{
		var row = new SessionRow(Session(stack: XamlStack.Wpf, reason: "it has loaded PresentationFramework.dll"));

		Assert.Contains("WPF", row.XamlFact);
		Assert.Contains("PresentationFramework.dll", row.XamlFact);
		Assert.Contains("Only UWP and WinUI", row.XamlFact);
	}

	/// <summary>
	/// Nothing having breathed yet and something breathing this instant are opposite facts, and the
	/// heartbeat is exactly where conflating them would mislead.
	/// </summary>
	[Test]
	public void Distinguishes_no_events_from_a_recent_one()
	{
		Assert.Equal("no events yet", new SessionRow(Session()).Heartbeat);
		Assert.Contains("0.4s ago", new SessionRow(Session(lastEventAge: TimeSpan.FromMilliseconds(420))).Heartbeat);
	}

	[Test]
	public void Says_who_will_resume_a_stopped_target()
	{
		var auto = new SessionRow(Session(stop: Stop()));
		Assert.Contains("auto-continues in", auto.ResumeLabel);

		var held = new SessionRow(Session(stop: Stop(resume: LiveStopResume.HeldByOperator)));
		Assert.Contains("held for you", held.ResumeLabel);
		Assert.True(held.IsHeld);
	}

	/// <summary>
	/// The heartbeat moves without anything being polled, which is the whole reason a tick exists:
	/// a reader watching a target they suspect has wedged should see the number climbing.
	/// </summary>
	[Test]
	public void Ages_its_heartbeat_between_polls()
	{
		var row = new SessionRow(Session(lastEventAge: TimeSpan.Zero));
		var read = DateTime.UtcNow;

		row.Tick(read.AddSeconds(45));

		Assert.Contains("45s ago", row.Heartbeat);
	}

	/// <summary>
	/// A window showing a tree has to know when an agent's apply moved it, and the activity history
	/// is the only signal either side has.
	/// </summary>
	[Test]
	public void Notices_an_apply_that_finished_between_polls()
	{
		var before = Session() with { Recent = [] };
		var after = Session() with
		{
			Recent =
			[
				new WorkerActivity
				{
					Id = 7,
					Operation = ToolNames.LiveAppXamlApply,
					StartedUtc = DateTime.UtcNow,
					Elapsed = TimeSpan.FromMilliseconds(120),
					Outcome = ActivityOutcome.Succeeded,
				},
			],
		};

		Assert.True(SessionRow.AppliedXaml(before, after));

		// And not twice: the same completed call seen again is not a second apply.
		Assert.False(SessionRow.AppliedXaml(after, after));
	}

	[Test]
	public void Ignores_an_apply_that_failed()
	{
		var before = Session() with { Recent = [] };
		var after = Session() with
		{
			Recent =
			[
				new WorkerActivity
				{
					Id = 7,
					Operation = ToolNames.LiveAppXamlApply,
					StartedUtc = DateTime.UtcNow,
					Elapsed = TimeSpan.FromMilliseconds(120),
					Outcome = ActivityOutcome.Failed,
					Error = "the provider did not answer",
				},
			],
		};

		Assert.False(SessionRow.AppliedXaml(before, after), "a failed apply moved nothing");
	}

	[Test]
	public void Names_the_processes_a_reader_would_go_looking_for()
	{
		var facts = new SessionRow(Session()).Facts;

		Assert.Contains("pid 9191", facts);
		Assert.Contains("host 4242", facts);
		Assert.Contains("x64", facts);
		Assert.Contains("up 3m", facts);
	}
}
