using System.Text.Json;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a live-app session says about itself over <c>GET /admin/sessions</c> and the operator API.
/// <para>
/// These are round trips rather than field-by-field assertions because the failure being guarded
/// against is a serialiser one. An enum written as a number is the case that has already happened
/// here once: a reader shown <c>"execution": 1</c> learns nothing, and the fields added for a
/// debugging UI are all enums, so every one of them is exposed to it.
/// </para>
/// </summary>
public sealed class LiveSessionReportTests
{
	/// <summary>A session holding a target at a breakpoint, with a person reading the stop.</summary>
	private static LiveAppSessionSummary Held() => new()
	{
		SessionId = "session-abcd1234",
		TargetDescription = "Widget.exe (launched)",
		Architecture = TargetArchitecture.X64,
		State = LiveAppSessionState.Ready,
		HostProcessId = 4242,
		TargetProcessId = 9191,
		StartedUtc = new DateTime(2026, 9, 10, 8, 30, 0, DateTimeKind.Utc),
		Uptime = TimeSpan.FromMinutes(3),
		Execution = LiveExecutionState.StoppedAtBreakpoint,
		Stop = new LiveStop
		{
			State = LiveExecutionState.StoppedAtBreakpoint,
			ThreadId = 12,
			BreakpointId = "bp-3",
			EventSequence = 57,
			StoppedAtUtc = new DateTime(2026, 9, 10, 8, 32, 0, DateTimeKind.Utc),
			Resume = LiveStopResume.HeldByOperator,
			ResumeDeadlineUtc = new DateTime(2026, 9, 10, 8, 37, 0, DateTimeKind.Utc),
		},
		XamlStack = XamlStack.WinUi,
		XamlStackReason = "it has loaded Microsoft.WinUI.dll, Microsoft.UI.Xaml.dll",
		XamlProvider = LiveXamlProvider.Resident,
		LastEventAge = TimeSpan.FromMilliseconds(420),
		InfoAge = TimeSpan.FromMilliseconds(900),
	};

	[Test]
	public void Writes_every_enum_as_a_word()
	{
		var json = JsonSerializer.Serialize(Held(), ContractJson.Options);

		Assert.Contains("\"execution\":\"StoppedAtBreakpoint\"", json);
		Assert.Contains("\"xamlStack\":\"WinUi\"", json);
		Assert.Contains("\"xamlProvider\":\"Resident\"", json);
		Assert.Contains("\"resume\":\"HeldByOperator\"", json);
	}

	/// <summary>
	/// The stop's own fields survive, and the sequence in particular. It is the stop's identity: a
	/// reader that cannot see it cannot tell a new stop from the same one polled again, which is what
	/// decides whether frames and locals have to be read afresh.
	/// </summary>
	[Test]
	public void Carries_the_stop_that_identifies_itself()
	{
		var json = JsonSerializer.Serialize(Held(), ContractJson.Options);
		var read = JsonSerializer.Deserialize<LiveAppSessionSummary>(json, ContractJson.Options);

		Assert.NotNull(read?.Stop);
		Assert.Equal(57, read!.Stop!.EventSequence);
		Assert.Equal(12, read.Stop.ThreadId);
		Assert.Equal("bp-3", read.Stop.BreakpointId);
		Assert.Equal(LiveStopResume.HeldByOperator, read.Stop.Resume);
		Assert.Equal(LiveExecutionState.StoppedAtBreakpoint, read.Execution);
	}

	/// <summary>
	/// A running target has no stop, and the absence is what says so rather than a stop describing
	/// itself as not stopped. <see cref="LiveExecutionState.Running"/> and a null stop are the same
	/// fact twice, deliberately: the state is the discriminator a caller switches on, and the null is
	/// what stops anything reading a deadline that means nothing.
	/// </summary>
	[Test]
	public void Says_a_running_target_has_no_stop()
	{
		var running = Held() with { Execution = LiveExecutionState.Running, Stop = null };

		var json = JsonSerializer.Serialize(running, ContractJson.Options);
		var read = JsonSerializer.Deserialize<LiveAppSessionSummary>(json, ContractJson.Options);

		Assert.Contains("\"execution\":\"Running\"", json);
		Assert.Null(read?.Stop);
	}

	/// <summary>
	/// The two ages are optional, and null means "never" rather than "zero". A session whose host has
	/// not answered yet has no age to report, and reporting zero would say the opposite -- that it
	/// answered a moment ago.
	/// </summary>
	[Test]
	public void Leaves_both_ages_absent_before_anything_has_been_asked()
	{
		var fresh = Held() with { LastEventAge = null, InfoAge = null };

		var read = JsonSerializer.Deserialize<LiveAppSessionSummary>(
			JsonSerializer.Serialize(fresh, ContractJson.Options), ContractJson.Options);

		Assert.Null(read?.LastEventAge);
		Assert.Null(read?.InfoAge);
	}
}
