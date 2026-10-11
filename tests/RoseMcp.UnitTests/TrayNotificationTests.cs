using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Tray;

using static RoseMcp.UnitTests.TrayStatusTests;

namespace RoseMcp.UnitTests;

/// <summary>
/// When the tray interrupts somebody, and what it says: a release once each, across runs; a failure once
/// each and not in a stream; what a restart did to the agents that came back across it; and an answer to a
/// note.
/// <para>
/// A notification said twice is how a person learns to ignore the icon, and one said for a start that was
/// not a restart is a notification that lies. Both are pinned here, since nothing drives the real icon.
/// </para>
/// </summary>
public sealed class TrayNotificationTests
{
	private static readonly BuildIdentity Build = new() { Version = "1.3.0", Commit = "d41a9e0c11d75a16a82274f8c52a9e33110afee4" };

	/// <summary>
	/// The release is remembered in a file, so a tray started again -- every sign-in and every deploy -- does not
	/// announce the release it already announced. A later release is announced in its turn.
	/// </summary>
	[Test]
	public void A_release_is_announced_once_across_runs()
	{
		var folder = Directory.CreateTempSubdirectory("rosemcp-tray-");

		try
		{
			var path = TrayMemoryFile.PathIn(folder.FullName);

			var first = TrayMemoryFile.Read(path);
			first.IsNewRelease(available: true, "1.4.0").ShouldBeTrue();
			TrayMemoryFile.Write(path, first with { NotifiedRelease = "1.4.0" }).ShouldBeTrue();

			var restarted = TrayMemoryFile.Read(path);
			restarted.IsNewRelease(available: true, "1.4.0").ShouldBeFalse();
			restarted.IsNewRelease(available: true, "v1.4.0").ShouldBeFalse();
			restarted.IsNewRelease(available: true, "1.5.0").ShouldBeTrue();
		}
		finally
		{
			folder.Delete(recursive: true);
		}
	}

	[Test]
	public void Nothing_is_announced_without_a_release()
	{
		var memory = new TrayMemory();

		memory.IsNewRelease(available: false, "1.4.0").ShouldBeFalse();
		memory.IsNewRelease(available: true, null).ShouldBeFalse();
		memory.IsNewRelease(available: true, string.Empty).ShouldBeFalse();
	}

	/// <summary>
	/// A memory that cannot be read is an empty one -- the worst it costs is a notification said twice -- and
	/// writing it leaves no staging file behind and keeps fields it does not change.
	/// </summary>
	[Test]
	public void The_memory_survives_a_file_that_is_missing_or_nonsense()
	{
		var folder = Directory.CreateTempSubdirectory("rosemcp-tray-");

		try
		{
			var path = TrayMemoryFile.PathIn(folder.FullName);
			TrayMemoryFile.Read(path).ShouldBe(new TrayMemory());

			File.WriteAllText(path, "{ not json");
			TrayMemoryFile.Read(path).ShouldBe(new TrayMemory());

			TrayMemoryFile.Write(path, new TrayMemory { NotifiedRelease = "1.4.0", LastCommit = "abc" }).ShouldBeTrue();
			TrayMemoryFile.Write(path, TrayMemoryFile.Read(path) with { LastCommit = "def" }).ShouldBeTrue();

			TrayMemoryFile.Read(path).ShouldBe(new TrayMemory { NotifiedRelease = "1.4.0", LastCommit = "def" });
			Directory.GetFiles(folder.FullName).Select(Path.GetFileName).ShouldBe([TrayMemoryFile.FileName]);
		}
		finally
		{
			folder.Delete(recursive: true);
		}
	}

	/// <summary>A handler failing on every poll is one notification, not one every two seconds.</summary>
	[Test]
	public void A_failure_is_announced_once()
	{
		var notices = new FailureNotices();
		var now = DateTimeOffset.UtcNow;

		notices.Admit("the disk is full", now).ShouldBeTrue();
		notices.Admit("the disk is full", now + TimeSpan.FromMinutes(10)).ShouldBeFalse();
	}

	/// <summary>
	/// Two different failures a moment apart are one notification; the first already sent the person to the log,
	/// which has both. One after the spacing is announced.
	/// </summary>
	[Test]
	public void Failures_are_spaced()
	{
		var notices = new FailureNotices();
		var now = DateTimeOffset.UtcNow;

		notices.Admit("first", now).ShouldBeTrue();
		notices.Admit("second", now + TimeSpan.FromSeconds(5)).ShouldBeFalse();
		notices.Admit("second", now + FailureNotices.Spacing + TimeSpan.FromSeconds(5)).ShouldBeFalse();
		notices.Admit("third", now + FailureNotices.Spacing).ShouldBeTrue();
	}

	/// <summary>
	/// No agent came back, so as far as any agent is concerned this was a start: nothing is said. That is the
	/// sign-in case, and the reason a restart is only ever said about agents.
	/// </summary>
	[Test]
	public void A_start_with_no_agents_says_nothing()
	{
		var report = new RestartReport();
		report.Observe([]);

		report.Notification(Build, previousCommit: "aaaaaaa1111").ShouldBeNull();
	}

	/// <summary>A relay that let go is not one that reconnected, and an http client with no relay never reconnects at all.</summary>
	[Test]
	public void Only_watching_relays_count_as_reconnected()
	{
		var report = new RestartReport();
		report.Observe([Client(named: true, watching: false), Client(named: false, watching: null)]);

		report.Notification(Build, previousCommit: null).ShouldBeNull();
	}

	[Test]
	public void A_restart_names_the_build_it_moved_from()
	{
		var report = new RestartReport();
		report.Observe([Client(named: true, watching: true, toolsCurrent: true, heldTools: "h1")]);

		var notification = report.Notification(Build, previousCommit: "aaaaaaa1111bbbb")!;

		notification.Kind.ShouldBe(TrayNotificationKind.Restart);
		notification.Title.ShouldBe("Rose restarted on d41a9e0 (was aaaaaaa)");
		notification.Body.ShouldBe("1 agent session reconnected. Its tools are unchanged.");
		notification.Action.ShouldBe(TrayAction.OpenRose);

		report.Notification(Build, previousCommit: "d41a9e0c11d75")!.Title.ShouldBe("Rose restarted on d41a9e0");
		report.Notification(Build, previousCommit: null)!.Title.ShouldBe("Rose restarted on d41a9e0");
	}

	/// <summary>
	/// A client whose tools changed says so until it relists; one that relisted between looks is caught by its
	/// hash changing. One still holding the old tools when the report is read is said apart.
	/// </summary>
	[Test]
	public void A_restart_says_whose_tools_changed()
	{
		var report = new RestartReport();

		report.Observe(
		[
			Client(named: true, watching: true, toolsCurrent: false, heldTools: "old", id: "a"),
			Client(named: true, watching: true, toolsCurrent: true, heldTools: "old", id: "b"),
			Client(named: true, watching: true, toolsCurrent: true, heldTools: "same", id: "c"),
		]);

		report.Observe(
		[
			Client(named: true, watching: true, toolsCurrent: false, heldTools: "old", id: "a"),
			Client(named: true, watching: true, toolsCurrent: true, heldTools: "new", id: "b"),
			Client(named: true, watching: true, toolsCurrent: true, heldTools: "same", id: "c"),
		]);

		report.Notification(Build, previousCommit: null)!.Body.ShouldBe(
			"3 agent sessions reconnected. Tools changed for 2 of them, and Rose asked them to list them again. "
				+ "1 has not listed the new tools yet.");
	}

	[Test]
	public void A_restart_where_every_tool_list_changed_says_so()
	{
		var report = new RestartReport();
		report.Observe(
		[
			Client(named: true, watching: true, toolsCurrent: false, id: "a"),
			Client(named: true, watching: true, toolsCurrent: false, id: "b"),
		]);
		report.Observe(
		[
			Client(named: true, watching: true, toolsCurrent: true, id: "a"),
			Client(named: true, watching: true, toolsCurrent: true, id: "b"),
		]);

		report.Notification(Build, previousCommit: null)!.Body
			.ShouldBe("2 agent sessions reconnected. Tools changed for all of them, and Rose asked them to list them again.");
	}

	/// <summary>Where a relay has not said whether its client's tools are current, nothing is claimed about them.</summary>
	[Test]
	public void A_restart_claims_nothing_it_was_not_told()
	{
		var report = new RestartReport();
		report.Observe([Client(named: true, watching: true, toolsCurrent: null, id: "a")]);

		report.Notification(Build, previousCommit: null)!.Body.ShouldBe("1 agent session reconnected.");
	}

	/// <summary>An agent that came back and then went again before the report is read is not counted among those that reconnected.</summary>
	[Test]
	public void A_restart_counts_the_agents_still_connected()
	{
		var report = new RestartReport();
		report.Observe([Client(named: true, watching: true, toolsCurrent: true, id: "a"), Client(named: true, watching: true, toolsCurrent: true, id: "b")]);
		report.Observe([Client(named: true, watching: true, toolsCurrent: true, id: "a")]);

		report.Notification(Build, previousCommit: null)!.Body.ShouldStartWith("1 agent session reconnected.");
	}

	/// <summary>
	/// The seam notes to an agent arrive by: an answer names the agent and opens RoseMCP, and one with nothing to
	/// quote still says it answered.
	/// </summary>
	[Test]
	public void An_answer_to_a_note_opens_RoseMCP()
	{
		var answered = TrayNotification.NoteAnswered("fix the build", "Done: the analyzer was stale.");

		answered.Kind.ShouldBe(TrayNotificationKind.NoteAnswered);
		answered.Title.ShouldBe("fix the build answered your note");
		answered.Body.ShouldBe("Done: the analyzer was stale.");
		answered.Action.ShouldBe(TrayAction.OpenNoteAnswers);

		TrayNotification.NoteAnswered("fix the build", null).Body.ShouldBe("Click to read it in RoseMCP.");
	}

	/// <summary>A release opens its page, and a failure, whether contained or one that stopped the start, opens its log.</summary>
	[Test]
	public void Each_notification_does_what_it_offers()
	{
		TrayNotification.Release("1.4.0").ShouldBe(new TrayNotification(
			TrayNotificationKind.Release,
			"RoseMCP 1.4.0 is out",
			"Click to read about it. Nothing is downloaded or installed until you choose to.",
			TrayAction.OpenRelease));

		TrayNotification.Failure("the disk is full.").Action.ShouldBe(TrayAction.OpenFailureLog);
		TrayNotification.CouldNotStart("port 5077 is in use").ShouldBe(new TrayNotification(
			TrayNotificationKind.Failure,
			"RoseMCP is not serving",
			"It could not start: port 5077 is in use. Click to open its log.",
			TrayAction.OpenFailureLog));
	}

	/// <summary>A balloon holds 64 characters of title and 256 of body; anything longer is cut here, with a mark that it was.</summary>
	[Test]
	public void A_notification_fits_a_balloon()
	{
		var balloon = TrayNotification.Worded(TrayNotificationKind.Restart, new string('t', 200), new string('b', 400), TrayAction.OpenRose);

		balloon.Title.Length.ShouldBe(TrayNotification.TitleLimit);
		balloon.Body.Length.ShouldBe(TrayNotification.BodyLimit);
		balloon.Body.ShouldEndWith("…");
	}
}
