using RoseMcp.Contracts;
using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// How a target somebody paused is described, as against one a breakpoint stopped.
/// <para>
/// The distinction is the point: a stack that looks like nothing in particular is expected when you
/// pressed Pause and alarming at a breakpoint, so a window that called both "a step" would be
/// answering the reader's first question wrongly.
/// </para>
/// </summary>
public sealed class PausedSessionTests
{
	[Test]
	public void A_paused_target_says_so_rather_than_calling_itself_a_step()
	{
		Assert.Equal(
			"paused on thread 4128",
			SessionRow.DescribeExecution(Stopped(LiveExecutionState.PausedByOperator, threadId: 4128)));

		Assert.Equal(
			"a step on thread 4128",
			SessionRow.DescribeExecution(Stopped(LiveExecutionState.StoppedAtStep, threadId: 4128)));
	}

	/// <summary>A breakpoint is named, because its id is what a reader removes or moves.</summary>
	[Test]
	public void A_breakpoint_still_names_itself()
	{
		Assert.Equal(
			"breakpoint bp-3 on thread 4128",
			SessionRow.DescribeExecution(Stopped(LiveExecutionState.StoppedAtBreakpoint, 4128, "bp-3")));
	}

	[Test]
	public void A_running_target_has_nothing_to_say_about_a_stop() =>
		Assert.Equal(string.Empty, SessionRow.DescribeExecution(Summary(null)));

	private static LiveAppSessionSummary Stopped(LiveExecutionState state, int? threadId, string? breakpointId = null) =>
		Summary(new LiveStop
		{
			State = state,
			ThreadId = threadId,
			BreakpointId = breakpointId,
			EventSequence = 11,
			StoppedAtUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
			Resume = LiveStopResume.AutoContinue,
			ResumeDeadlineUtc = new DateTime(2026, 3, 1, 12, 0, 30, DateTimeKind.Utc),
		});

	private static LiveAppSessionSummary Summary(LiveStop? stop) => new()
	{
		SessionId = "session-1",
		TargetDescription = "Probe (pid 1)",
		Architecture = TargetArchitecture.Arm64,
		State = LiveAppSessionState.Ready,
		StartedUtc = new DateTime(2026, 3, 1, 11, 59, 0, DateTimeKind.Utc),
		Uptime = TimeSpan.FromMinutes(1),
		Execution = stop?.State ?? LiveExecutionState.Running,
		Stop = stop,
		XamlStack = XamlStack.Unknown,
		XamlStackReason = "not a XAML app",
		XamlProvider = LiveXamlProvider.None,
	};
}
