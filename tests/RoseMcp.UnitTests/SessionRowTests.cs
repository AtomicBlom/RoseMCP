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

		row.StateLabel.ShouldBe("Running");
		row.IsHealthy.ShouldBeTrue();
		row.IsStopped.ShouldBeFalse();
		row.ExecutionLabel.ShouldBe(string.Empty);
		row.ResumeLabel.ShouldBe(string.Empty);
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

		row.StateLabel.ShouldBe("Stopped");
		row.IsCaution.ShouldBeTrue();
		row.IsStopped.ShouldBeTrue();
		row.StopSequence.ShouldBe(57);
		row.ExecutionLabel.ShouldBe("breakpoint bp-3 on thread 12");
	}

	/// <summary>A step is a stop with no breakpoint, and says so rather than leaving a gap.</summary>
	[Test]
	public void Names_a_step_rather_than_a_missing_breakpoint()
	{
		var row = new SessionRow(Session(stop: Stop(LiveExecutionState.StoppedAtStep, breakpointId: null)));

		row.ExecutionLabel.ShouldBe("a step on thread 12");
	}

	[Test]
	[Arguments(LiveAppSessionState.Starting, "Starting")]
	[Arguments(LiveAppSessionState.Faulted, "Faulted")]
	[Arguments(LiveAppSessionState.Ended, "Ended")]
	public void Reads_each_lifecycle_state(LiveAppSessionState state, string expected) =>
		new SessionRow(Session(state)).StateLabel.ShouldBe(expected);

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
		new SessionRow(Session(stack: stack)).HasXaml.ShouldBe(expected);

	[Test]
	public void Names_the_framework_and_whether_a_read_is_cheap()
	{
		var resident = new SessionRow(Session(stack: XamlStack.WinUi, provider: LiveXamlProvider.Resident));
		resident.XamlFact.ShouldContain("WinUI 3", Case.Sensitive);
		resident.XamlFact.ShouldContain("provider resident", Case.Sensitive);

		var cold = new SessionRow(Session(stack: XamlStack.Uwp));
		cold.XamlFact.ShouldContain("UWP", Case.Sensitive);
		cold.XamlFact.ShouldContain("not injected yet", Case.Sensitive);

		// A provider that has gone is a channel failing quietly, and the next read pays an injection.
		var lost = new SessionRow(Session(stack: XamlStack.Uwp, provider: LiveXamlProvider.Lost));
		lost.XamlFact.ShouldContain("injects again", Case.Sensitive);
	}

	/// <summary>
	/// A target with no tap carries the reason its stack was decided, because the alternative is a
	/// reader concluding the tool is broken.
	/// </summary>
	[Test]
	public void Says_why_a_target_has_no_visual_tree()
	{
		var row = new SessionRow(Session(stack: XamlStack.Wpf, reason: "it has loaded PresentationFramework.dll"));

		row.XamlFact.ShouldContain("WPF", Case.Sensitive);
		row.XamlFact.ShouldContain("PresentationFramework.dll", Case.Sensitive);
		row.XamlFact.ShouldContain("Only UWP and WinUI", Case.Sensitive);
	}

	/// <summary>
	/// Nothing having breathed yet and something breathing this instant are opposite facts, and the
	/// heartbeat is exactly where conflating them would mislead.
	/// </summary>
	[Test]
	public void Distinguishes_no_events_from_a_recent_one()
	{
		new SessionRow(Session()).Heartbeat.ShouldBe("no events yet");
		new SessionRow(Session(lastEventAge: TimeSpan.FromMilliseconds(420))).Heartbeat.ShouldContain("0.4s ago", Case.Sensitive);
	}

	[Test]
	public void Says_who_will_resume_a_stopped_target()
	{
		var auto = new SessionRow(Session(stop: Stop()));
		auto.ResumeLabel.ShouldContain("auto-continues in", Case.Sensitive);

		var held = new SessionRow(Session(stop: Stop(resume: LiveStopResume.HeldByOperator)));
		held.ResumeLabel.ShouldContain("held for you", Case.Sensitive);
		held.IsHeld.ShouldBeTrue();
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

		row.Heartbeat.ShouldContain("45s ago", Case.Sensitive);
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

		SessionRow.AppliedXaml(before, after).ShouldBeTrue();

		// And not twice: the same completed call seen again is not a second apply.
		SessionRow.AppliedXaml(after, after).ShouldBeFalse();
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

		SessionRow.AppliedXaml(before, after).ShouldBeFalse("a failed apply moved nothing");
	}

	[Test]
	public void Names_the_processes_a_reader_would_go_looking_for()
	{
		var facts = new SessionRow(Session()).Facts;

		facts.ShouldContain("pid 9191", Case.Sensitive);
		facts.ShouldContain("host 4242", Case.Sensitive);
		facts.ShouldContain("x64", Case.Sensitive);
		facts.ShouldContain("up 3m", Case.Sensitive);
	}
}
