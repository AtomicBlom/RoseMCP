using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The one line the first result after a restart carries, and the parts that decide what it can
/// honestly say: which debug sessions were open, and that it is said exactly once however many
/// calls finish at the same moment.
/// </summary>
public sealed class RestartNoticeTests
{
	private static readonly BuildIdentity Build = new() { Version = "1.3.0", Commit = new string('d', 40) };

	[Test]
	public void A_restart_names_the_build_and_that_solutions_load_again()
	{
		var line = new RestartNotice { Build = Build, Restarted = true, Tools = Unchanged }.Describe();

		line.ShouldBe("RoseMCP restarted and is now build 1.3.0 at ddddddd; solutions it had loaded load again on the first call that needs them.");
	}

	[Test]
	public void Names_the_debug_sessions_that_ended_and_what_each_was_debugging()
	{
		var line = new RestartNotice
		{
			Build = Build,
			Restarted = true,
			EndedSessions = [new("session-1", "Probe.exe"), new("session-2", "Calculator (UWP)")],
			Tools = Unchanged,
		}.Describe();

		line.ShouldContain("the debug sessions this session had open ended with it: session-1 (Probe.exe), session-2 (Calculator (UWP))", Case.Sensitive);
	}

	/// <summary>A long list of changed tools is named up to a point and counted after it, so the line stays a line.</summary>
	[Test]
	public void Counts_the_tools_past_the_first_few()
	{
		var changed = Enumerable.Range(1, 8).Select(index => $"rose_{index}").ToList();
		var line = new RestartNotice
		{
			Build = Build,
			Restarted = true,
			Tools = new ToolSurfaceChange([], [], changed, InstructionsChanged: false),
		}.Describe();

		line.ShouldContain("changed rose_1, rose_2, rose_3, rose_4, rose_5 and 3 more", Case.Sensitive);
	}

	[Test]
	public void Says_when_the_tools_could_not_be_read()
	{
		var line = new RestartNotice { Build = Build, Restarted = true, Tools = null }.Describe();

		line.ShouldContain("its tool list could not be read, so the client was asked to list the tools again", Case.Sensitive);
	}

	[Test]
	public void An_unknown_commit_is_said_rather_than_left_out()
	{
		var line = new RestartNotice { Build = new BuildIdentity { Version = "1.3.0" }, Restarted = true, Tools = Unchanged }.Describe();

		line.ShouldStartWith("RoseMCP restarted and is now build 1.3.0, commit unknown", Case.Sensitive);
	}

	/// <summary>
	/// Two reconnects before any result: the later build, every session either one ended, and the
	/// earlier tool change kept where the later one found nothing new.
	/// </summary>
	[Test]
	public void A_second_restart_folds_into_the_first()
	{
		var first = new RestartNotice
		{
			Build = new BuildIdentity { Version = "1.3.0", Commit = new string('a', 40) },
			Restarted = true,
			EndedSessions = [new("session-1", "Probe.exe")],
			Tools = new ToolSurfaceChange(["rose_new"], [], [], InstructionsChanged: false),
		};

		var second = new RestartNotice { Build = Build, Restarted = true, EndedSessions = [new("session-1", "Probe.exe")], Tools = Unchanged };

		var merged = first.Then(second);

		merged.Build.ShouldBe(Build);
		merged.EndedSessions.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
		merged.Tools!.Added.ShouldBe(["rose_new"]);
	}

	/// <summary>
	/// Many calls finishing as the broker comes back: exactly one of them gets the line, and a notice
	/// added while they race is neither lost nor said twice.
	/// </summary>
	[Test]
	public async Task A_waiting_notice_is_claimed_by_exactly_one_caller()
	{
		var pending = new PendingNotice();
		pending.Add(new RestartNotice { Build = Build, Restarted = true, Tools = Unchanged });

		using var start = new ManualResetEventSlim();
		var claims = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				start.Wait();
				return pending.Claim();
			}))
			.ToList();

		start.Set();
		var claimed = await Task.WhenAll(claims);

		claimed.Count(notice => notice is not null).ShouldBe(1);
		pending.Claim().ShouldBeNull("nothing is left once it has been claimed");
	}

	[Test]
	public void Reads_the_open_sessions_out_of_a_debug_listing_and_leaves_ended_ones_out()
	{
		var listing = new CallToolResult
		{
			StructuredContent = JsonDocument.Parse(
				"""
				{"sessions":[
					{"sessionId":"session-1","targetDescription":"Probe.exe","state":"Ready"},
					{"sessionId":"session-2","targetDescription":"Gone.exe","state":"Ended"},
					{"sessionId":"session-3","state":"Starting"}
				]}
				""").RootElement.Clone(),
		};

		DebugSessionMemory.Read(listing).ShouldBe([new("session-1", "Probe.exe"), new("session-3", "an unnamed target")]);
		DebugSessionMemory.Read(new CallToolResult { IsError = true }).ShouldBeEmpty();
	}

	/// <summary>
	/// A reading that finishes after a later one does not overwrite it, and a reading still in flight
	/// when the sessions are taken -- because the broker went -- is dropped.
	/// </summary>
	[Test]
	public void An_older_reading_never_replaces_a_newer_one()
	{
		var memory = new DebugSessionMemory();

		var older = memory.Begin();
		var newer = memory.Begin();

		memory.Record(newer, [new("session-2", "New.exe")]);
		memory.Record(older, [new("session-1", "Old.exe")]);

		var inFlight = memory.Begin();
		memory.Take().ShouldBe([new("session-2", "New.exe")]);

		memory.Record(inFlight, [new("session-3", "Late.exe")]);
		memory.Take().ShouldBeEmpty("a reading begun before the take is of the broker that has gone");
	}

	[Test]
	public void Only_the_calls_that_start_or_end_a_session_change_what_is_open()
	{
		DebugSessionMemory.Changes(ToolNames.DebugAttach).ShouldBeTrue();
		DebugSessionMemory.Changes(ToolNames.DebugLaunch).ShouldBeTrue();
		DebugSessionMemory.Changes(ToolNames.DebugLaunchUwp).ShouldBeTrue();
		DebugSessionMemory.Changes(ToolNames.DebugDetach).ShouldBeTrue();
		DebugSessionMemory.Changes(ToolNames.DebugList).ShouldBeFalse();
		DebugSessionMemory.Changes(ToolNames.WorkspaceStatus).ShouldBeFalse();
	}

	private static ToolSurfaceChange Unchanged => new([], [], [], InstructionsChanged: false);
}
