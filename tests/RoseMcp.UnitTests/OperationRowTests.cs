using System.Collections.ObjectModel;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// An operation as the RoseMCP window lists it, and the one order every list of them keeps: running calls first,
/// then finished ones by when they finished, then by when they started, the duration always in the right-hand
/// column -- and, opened, the arguments and response formatted and a write's diff.
/// </summary>
public sealed class OperationRowTests
{
	private static readonly DateTimeOffset Noon = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// A call that started long ago and finished a moment ago is the news, so it sits above one that started later and
	/// finished earlier; running calls sit above both, and two that finished together are told apart by their start.
	/// </summary>
	[Test]
	public void Running_calls_come_first_then_finished_ones_by_when_they_finished()
	{
		var longCall = Call("long", start: Noon.AddMinutes(-10), finish: Noon.AddSeconds(-1));
		var quickCall = Call("quick", start: Noon.AddMinutes(-2), finish: Noon.AddMinutes(-2).AddSeconds(1));
		var olderRunning = Call("running-old", start: Noon.AddMinutes(-3));
		var newerRunning = Call("running-new", start: Noon.AddSeconds(-5));
		var tiedEarly = Call("tied-early", start: Noon.AddMinutes(-20), finish: Noon.AddMinutes(-5));
		var tiedLate = Call("tied-late", start: Noon.AddMinutes(-6), finish: Noon.AddMinutes(-5));

		var sorted = OperationOrder.Sort([quickCall, tiedEarly, longCall, olderRunning, tiedLate, newerRunning]);

		sorted.Select(entry => entry.Id).ShouldBe(["running-new", "running-old", "long", "quick", "tied-late", "tied-early"]);
	}

	/// <summary>A broker starting is a marker where it started, not a gap: it sorts among the finished calls by that time.</summary>
	[Test]
	public void A_restart_is_a_marker_where_it_happened()
	{
		var before = Call("before", start: Noon.AddMinutes(-30), finish: Noon.AddMinutes(-29));
		var restart = Started("restart", Noon.AddMinutes(-20));
		var after = Call("after", start: Noon.AddMinutes(-10), finish: Noon.AddMinutes(-9));

		OperationOrder.Sort([before, after, restart]).Select(entry => entry.Id).ShouldBe(["after", "restart", "before"]);

		var row = new OperationRow(restart, Noon);
		row.IsMarker.ShouldBeTrue();
		row.MarkerText.ShouldContain("Rose started");
		row.MarkerText.ShouldContain("pid 4242");
		row.Duration.ShouldBeEmpty();
	}

	/// <summary>
	/// A call that finishes moves from the running ones to the top of the finished ones, and the row that moves is the
	/// same row: one a reader opened stays open with what it read.
	/// </summary>
	[Test]
	public void Merging_moves_a_finished_call_and_keeps_its_row()
	{
		var rows = new ObservableCollection<OperationRow>();
		var finished = Call("finished", start: Noon.AddMinutes(-1), finish: Noon.AddSeconds(-30));
		var running = Call("running", start: Noon.AddMinutes(-5));

		OperationOrder.Merge(rows, [finished, running], Noon);
		rows.Select(row => row.Id).ShouldBe(["running", "finished"]);

		var opened = rows[0];
		opened.ShowBody(running with { Arguments = "{\"symbol\":\"Ink\"}" });

		var nowFinished = running with { Outcome = ActivityOutcome.Succeeded, FinishedUtc = Noon };
		OperationOrder.Merge(rows, [finished, nowFinished], Noon);

		rows.Select(row => row.Id).ShouldBe(["running", "finished"], "it finished last, so it is the newest finished call");
		rows[0].ShouldBeSameAs(opened);
		rows[0].IsRunning.ShouldBeFalse();
		rows[0].Arguments.ShouldContain("\"symbol\": \"Ink\"");
	}

	/// <summary>
	/// The time column is when a call finished, or when a running one started; the duration is the last column,
	/// counting up while it runs and marked as still going.
	/// </summary>
	[Test]
	public void The_duration_counts_up_while_a_call_runs()
	{
		var running = Call("r", start: Noon.AddSeconds(-2));
		var row = new OperationRow(running, Noon);

		row.Time.ShouldBe(OperationRow.Clock(running.StartedUtc));
		row.Duration.ShouldBe("2.0s…");
		row.Glyph.ShouldBe("◷");

		row.Tick(Noon.AddSeconds(3));
		row.Duration.ShouldBe("5.0s…");

		var done = running with { Outcome = ActivityOutcome.Succeeded, FinishedUtc = Noon.AddSeconds(1) };
		row.Update(done, Noon.AddMinutes(5));

		row.Duration.ShouldBe("3.0s", "a finished call's duration stops where it finished");
		row.Time.ShouldBe(OperationRow.Clock(done.FinishedUtc!.Value));
		row.Glyph.ShouldBe("✓");
	}

	/// <summary>A failure says why on the row, its first line, marked as cut where it was.</summary>
	[Test]
	public void A_failure_says_why_on_the_row()
	{
		var failed = Call("f", start: Noon.AddSeconds(-1), finish: Noon) with
		{
			Outcome = ActivityOutcome.Failed,
			Error = "Leaves CS0103: the name 'origin' does not exist.\nat line 41",
		};

		var row = new OperationRow(failed, Noon);

		row.Failed.ShouldBeTrue();
		row.Glyph.ShouldBe("✗");
		row.Error.ShouldBe("Leaves CS0103: the name 'origin' does not exist.…");
	}

	/// <summary>
	/// Opened, a row shows its arguments and response indented, the arguments as sent for copying, a write's diff a
	/// line at a time, what was cut and how long it really was, and the id every log line for the call carries.
	/// </summary>
	[Test]
	public void An_opened_row_shows_its_body_formatted()
	{
		var entry = Call("w", start: Noon.AddSeconds(-1), finish: Noon) with
		{
			Tool = "rose_replace_body",
			Applied = true,
			CorrelationId = "abc123",
			Arguments = "{\"member\":\"Describe\",\"code\":\"<T>\"}",
			Response = "{\"applied\":true}",
			ResponseFullLength = 90_000,
			Changes =
			[
				new ActivityFileChange
				{
					FilePath = @"D:\repo\src\WorkerActivity.cs",
					Lines = "38-43",
					Diff = "--- a/WorkerActivity.cs\n+++ b/WorkerActivity.cs\n@@ -40,2 +40,3 @@\n var verb = x;\n-return a;\n+return b;\n",
				},
			],
		};

		var row = new OperationRow(entry with { Arguments = null, Response = null, Changes = [] }, Noon);
		row.ShowBody(entry);

		row.ArgumentsJson.ShouldBe(entry.Arguments);
		row.Arguments.ShouldContain("\"code\": \"<T>\"", Case.Sensitive, "source is shown as written, not escaped");
		row.Response.ShouldContain("\"applied\": true");
		row.Notes.ShouldContain("90,000");
		row.CallId.ShouldContain("abc123");

		var change = row.Changes.ShouldHaveSingleItem();
		change.FileName.ShouldBe("WorkerActivity.cs");
		change.Facts.ShouldContain("lines 38-43");
		change.Diff.Select(line => line.Kind).ShouldBe(
		[
			DiffLineKind.Header,
			DiffLineKind.Header,
			DiffLineKind.Hunk,
			DiffLineKind.Context,
			DiffLineKind.Removed,
			DiffLineKind.Added,
		]);
	}

	/// <summary>A row opened while its call ran is read again once the call has finished, since what it read said nothing of how it ended.</summary>
	[Test]
	public void A_body_read_while_running_is_read_again_once_finished()
	{
		var running = Call("r", start: Noon.AddSeconds(-2));
		var row = new OperationRow(running, Noon);

		row.NeedsBody.ShouldBeFalse("a closed row reads nothing");

		_ = row.ToggleAsync((_, _) => Task.FromResult(running), CancellationToken.None);
		row.IsExpanded.ShouldBeTrue();
		row.NeedsBody.ShouldBeFalse("it has the body it read");
		row.Notes.ShouldContain("still running");

		row.Update(running with { Outcome = ActivityOutcome.Succeeded, FinishedUtc = Noon }, Noon);
		row.NeedsBody.ShouldBeTrue();
	}

	/// <summary>A body that cannot be read is said on the row rather than thrown over the window.</summary>
	[Test]
	public async Task A_body_that_cannot_be_read_is_said_on_the_row()
	{
		var row = new OperationRow(Call("gone", start: Noon.AddDays(-9), finish: Noon.AddDays(-9)), Noon);

		await row.ToggleAsync((_, _) => throw new KeyNotFoundException("The activity record has no entry gone."), CancellationToken.None);

		row.HasBodyProblem.ShouldBeTrue();
		row.BodyProblem.ShouldContain("has no entry gone");
		row.IsLoading.ShouldBeFalse();
	}

	[Test]
	[Arguments("{\"a\":1}", "{\n  \"a\": 1\n}")]
	[Arguments("not json", "not json")]
	[Arguments("{\"cut\": [1, 2", "{\"cut\": [1, 2")]
	[Arguments("", "")]
	public void Json_is_indented_where_it_is_json(string text, string expected) =>
		JsonText.Pretty(text).ReplaceLineEndings("\n").ShouldBe(expected);

	internal static ActivityEntry Call(string id, DateTimeOffset start, DateTimeOffset? finish = null) => new()
	{
		Id = id,
		Kind = ActivityEntryKind.Call,
		Broker = "b1",
		StartedUtc = start,
		FinishedUtc = finish,
		Tool = "rose_find_references",
		Target = "ActivityLog.Begin",
		Outcome = finish is null ? ActivityOutcome.Running : ActivityOutcome.Succeeded,
	};

	internal static ActivityEntry Started(string id, DateTimeOffset at) => new()
	{
		Id = id,
		Kind = ActivityEntryKind.BrokerStarted,
		Broker = "b2",
		StartedUtc = at,
		BrokerBuild = "1.3.0+" + new string('c', 40),
		BrokerProcessId = 4242,
	};
}
