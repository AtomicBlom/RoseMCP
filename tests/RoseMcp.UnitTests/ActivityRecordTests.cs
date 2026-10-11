using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The activity record: every call kept with its client, routing, outcome and bodies, on disk, past a restart,
/// for as long as the retention says, and read back by client, workspace, session, tool and time.
/// </summary>
public sealed class ActivityRecordTests : IDisposable
{
	private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

	private static readonly string Solution = Path.Combine(Path.GetTempPath(), "rose-activity", "Shop", "Shop.slnx");

	private readonly string _directory = Path.Combine(Path.GetTempPath(), "rose-activity-tests", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}

	[Test]
	public async Task A_call_is_kept_with_its_tool_arguments_routing_outcome_and_response(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);

		using (var call = record.Begin("rose_outline", Arguments(("filePath", "Shop.cs")), relayed: null, transportSession: null, "direct", origin: null))
		{
			CallRecording.Current.ShouldBeSameAs(call);
			call.Routed(workspace: Solution, workspaceKey: "Shop-1234", target: "Shop.cs");
			call.Finish(Answer(new { revision = 7, workspace = Solution }));
		}

		await record.FlushAsync(cancellationToken);

		var page = record.Query(new ActivityQuery());
		page.Bodies.ShouldBeFalse();
		page.Matched.ShouldBe(2, "the broker starting, and the call");

		var listed = page.Entries.Single(entry => entry.Kind == ActivityEntryKind.Call);
		listed.Tool.ShouldBe("rose_outline");
		listed.Workspace.ShouldBe(Solution);
		listed.WorkspaceKey.ShouldBe("Shop-1234");
		listed.Target.ShouldBe("Shop.cs");
		listed.Outcome.ShouldBe(ActivityOutcome.Succeeded);
		listed.Revision.ShouldBe(7);
		listed.ClientName.ShouldBe("direct", "an unnamed client is named by its transport's clientInfo");
		listed.FinishedUtc.ShouldNotBeNull();
		listed.Arguments.ShouldBeNull("a listing leaves the bodies out");
		listed.Response.ShouldBeNull();

		var found = record.Find(listed.Id).ShouldNotBeNull();
		JsonDocument.Parse(found.Arguments!).RootElement.GetProperty("filePath").GetString().ShouldBe("Shop.cs");
		JsonDocument.Parse(found.Response!).RootElement.GetProperty("revision").GetInt32().ShouldBe(7);

		Directory.GetFiles(_directory, "*.jsonl").ShouldHaveSingleItem();
	}

	/// <summary>A restart forgets nothing on disk, and is a line in the record rather than a gap in it.</summary>
	[Test]
	public async Task The_record_survives_a_restart_and_says_the_broker_started_again(CancellationToken cancellationToken)
	{
		string first;

		await using (var before = Open(out _))
		{
			first = before.Broker;
			Record(before, "rose_diagnostics");
			await before.FlushAsync(cancellationToken);
		}

		await using var after = Open(out _);
		Record(after, "rose_outline");
		await after.FlushAsync(cancellationToken);

		after.Broker.ShouldNotBe(first);

		var entries = after.Query(new ActivityQuery()).Entries;
		entries.Where(entry => entry.Kind == ActivityEntryKind.BrokerStarted).Select(entry => entry.Broker).ShouldBe([after.Broker, first]);
		entries.Single(entry => entry.Tool == "rose_diagnostics").Broker.ShouldBe(first);
		entries.Single(entry => entry.Tool == "rose_outline").Broker.ShouldBe(after.Broker);
		entries.Single(entry => entry.Kind == ActivityEntryKind.BrokerStarted && entry.Broker == first).BrokerProcessId.ShouldBe(Environment.ProcessId);

		Directory.GetFiles(_directory, "*.jsonl").Length.ShouldBe(2, "each broker run writes a file of its own");
	}

	[Test]
	public async Task A_failed_call_keeps_what_the_caller_was_told(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);

		using (var thrown = record.Begin("rose_rename_symbol", null, null, null, null, null))
		{
			thrown.Fail("The symbol Shop.Cart was not found.");
		}

		using (var answered = record.Begin("rose_outline", null, null, null, null, null))
		{
			answered.Finish(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "No such file." }] });
		}

		using (var abandoned = record.Begin("rose_debug_events", null, null, null, null, null))
		{
			abandoned.Cancel();
		}

		using (record.Begin("rose_find_references", null, null, null, null, null))
		{
			// Disposed without saying how it ended.
		}

		await record.FlushAsync(cancellationToken);

		var calls = record.Query(new ActivityQuery()).Entries.Where(entry => entry.Kind == ActivityEntryKind.Call).ToDictionary(entry => entry.Tool!);
		calls["rose_rename_symbol"].Outcome.ShouldBe(ActivityOutcome.Failed);
		calls["rose_rename_symbol"].Error.ShouldBe("The symbol Shop.Cart was not found.");
		calls["rose_outline"].Outcome.ShouldBe(ActivityOutcome.Failed);
		calls["rose_outline"].Error.ShouldBe("No such file.");
		calls["rose_debug_events"].Outcome.ShouldBe(ActivityOutcome.Cancelled);
		calls["rose_find_references"].Outcome.ShouldBe(ActivityOutcome.Cancelled);
	}

	[Test]
	public async Task A_response_past_the_ceiling_is_cut_and_says_how_long_it_was(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		var huge = new string('x', ActivityBodies.ResponseCeiling * 2);

		using (var call = record.Begin("rose_read_generated_document", null, null, null, null, null))
		{
			call.Finish(new CallToolResult { Content = [new TextContentBlock { Text = huge }] });
		}

		await record.FlushAsync(cancellationToken);

		var id = record.Query(new ActivityQuery { Tool = "rose_read_generated_document" }).Entries.ShouldHaveSingleItem().Id;
		var entry = record.Find(id).ShouldNotBeNull();

		entry.Response!.Length.ShouldBe(ActivityBodies.ResponseCeiling);
		entry.ResponseFullLength.ShouldBe(huge.Length);
		entry.Notes.ShouldHaveSingleItem().ShouldContain("The response is 64,000 characters", Case.Sensitive);
	}

	/// <summary>A listing carries the error where it carries no body, so an error is cut sooner than a response, and says so.</summary>
	[Test]
	public async Task An_error_past_its_ceiling_is_cut_and_says_how_long_it_was(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		var told = new string('e', ActivityBodies.ErrorCeiling + 500);

		using (var call = record.Begin("rose_diagnostics", null, null, null, null, null))
		{
			call.Fail(told);
		}

		await record.FlushAsync(cancellationToken);

		var listed = Calls(record, new ActivityQuery()).ShouldHaveSingleItem();
		listed.Error!.Length.ShouldBe(ActivityRecord.ErrorExcerpt, "a listing carries an excerpt");
		listed.ErrorFullLength.ShouldBe(ActivityBodies.ErrorCeiling);

		var entry = record.Find(listed.Id).ShouldNotBeNull();
		entry.Error!.Length.ShouldBe(ActivityBodies.ErrorCeiling);
		entry.ErrorFullLength.ShouldBe(told.Length);
		entry.Notes.ShouldHaveSingleItem().ShouldContain("The error is 4,500 characters", Case.Sensitive);
	}

	/// <summary>A write keeps each file it changed with that file's own part of the diff, named absolute as the worker named it.</summary>
	[Test]
	public async Task A_write_keeps_each_changed_files_diff(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		var cart = Path.Combine(Path.GetDirectoryName(Solution)!, "Cart.cs");
		var order = Path.Combine(Path.GetDirectoryName(Solution)!, "Order.cs");
		var cartDiff = $"--- {cart}\n+++ {cart}\n@@ -1,1 +1,1 @@\n-old\n+new\n";
		var orderDiff = $"--- /dev/null\n+++ {order}\n@@ -0,0 +1,1 @@\n+class Order;\n";

		using (var call = record.Begin("rose_format", null, null, null, null, null))
		{
			call.Wrote(new FormatResult
			{
				Revision = 3,
				FilesInspected = 2,
				Applied = true,
				ChangedFiles = [new ChangedFile { FilePath = cart, Lines = "1" }, new ChangedFile { FilePath = order, Created = true }],
				Diff = cartDiff + orderDiff,
			});

			call.Finish(Answer(new { revision = 3 }));
		}

		await record.FlushAsync(cancellationToken);

		var listed = record.Query(new ActivityQuery()).Entries.Single(entry => entry.Kind == ActivityEntryKind.Call);
		listed.Applied.ShouldBe(true);
		listed.Changes.Select(change => change.FilePath).ShouldBe([cart, order]);
		listed.Changes.ShouldAllBe(change => change.Diff == null, "a listing leaves the diffs out");

		var changes = record.Find(listed.Id).ShouldNotBeNull().Changes;
		changes[0].Diff.ShouldBe(cartDiff);
		changes[0].Lines.ShouldBe("1");
		changes[1].Diff.ShouldBe(orderDiff);
		changes[1].Created.ShouldBe(true);
	}

	[Test]
	public async Task Files_past_the_retention_are_deleted_when_the_record_starts(CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_directory);
		var old = Path.Combine(_directory, "20261005-090000-aaaaaaaaaaaa.jsonl");
		var recent = Path.Combine(_directory, "20261010-090000-bbbbbbbbbbbb.jsonl");
		var other = Path.Combine(_directory, "notes.txt");

		foreach (var path in (string[])[old, recent, other]) await File.WriteAllTextAsync(path, string.Empty, cancellationToken);

		File.SetLastWriteTimeUtc(old, (Start - TimeSpan.FromDays(3)).UtcDateTime);
		File.SetLastWriteTimeUtc(recent, (Start - TimeSpan.FromDays(1)).UtcDateTime);
		File.SetLastWriteTimeUtc(other, (Start - TimeSpan.FromDays(30)).UtcDateTime);

		await using var record = Open(out _, retention: TimeSpan.FromDays(2));
		await record.FlushAsync(cancellationToken);

		File.Exists(old).ShouldBeFalse("it was last written three days ago, and the record keeps two");
		File.Exists(recent).ShouldBeTrue();
		File.Exists(other).ShouldBeTrue("only the record's own files are its to prune");
		record.Retention.ShouldBe(TimeSpan.FromDays(2));
	}

	[Test]
	public async Task A_retention_that_says_nothing_usable_is_a_week()
	{
		await using var record = Open(out _, retention: TimeSpan.Zero);

		record.Retention.ShouldBe(TimeSpan.FromDays(7));
	}

	[Test]
	public async Task Entries_are_read_by_client_workspace_session_tool_and_time(CancellationToken cancellationToken)
	{
		await using var record = Open(out var clock);
		var mine = CallSession.Mint();
		var theirs = CallSession.Mint();

		record.Clients.Called(CallSession.Relayed(mine), null, "rose-relay", "1.0");

		Record(record, "rose_outline", relayed: CallSession.Relayed(mine), workspace: Solution);
		clock.Advance(TimeSpan.FromMinutes(1));
		Record(record, "rose_diagnostics", relayed: CallSession.Relayed(theirs), workspace: Solution);
		clock.Advance(TimeSpan.FromMinutes(1));
		Record(record, "rose_debug_events", relayed: CallSession.Relayed(mine), session: "session-1");
		clock.Advance(TimeSpan.FromMinutes(1));
		Record(record, "rose_outline", relayed: CallSession.Relayed(theirs));

		await record.FlushAsync(cancellationToken);

		var myId = record.Clients.Of(CallSession.Relayed(mine), null).ShouldNotBeNull().Id;
		var byId = Calls(record, new ActivityQuery { Client = myId });
		byId.Select(entry => entry.Tool).ShouldBe(["rose_debug_events", "rose_outline"]);
		byId.ShouldAllBe(entry => entry.ClientId == myId);

		var key = byId[0].ClientKey!;
		key.ShouldNotContain(mine, Case.Insensitive);
		Calls(record, new ActivityQuery { Client = key }).Count.ShouldBe(2, "the key says which client across a restart, where the row's id does not");

		Calls(record, new ActivityQuery { Workspace = Solution }).Select(entry => entry.Tool).ShouldBe(["rose_diagnostics", "rose_outline"]);
		Calls(record, new ActivityQuery { Workspace = "shop-1234" }).Count.ShouldBe(2, "a workspace's key is matched as well as its path");
		Calls(record, new ActivityQuery { DebugSession = "session-1" }).ShouldHaveSingleItem().Tool.ShouldBe("rose_debug_events");
		Calls(record, new ActivityQuery { Tool = "rose_outline" }).Count.ShouldBe(2);

		var window = Calls(record, new ActivityQuery { Since = Start + TimeSpan.FromMinutes(1), Until = Start + TimeSpan.FromMinutes(3) });
		window.Select(entry => entry.Tool).ShouldBe(["rose_debug_events", "rose_diagnostics"]);

		var limited = record.Query(new ActivityQuery { Tool = "rose_outline", Limit = 1 });
		limited.Entries.ShouldHaveSingleItem().ClientKey.ShouldNotBe(key, "the newest first");
		limited.Matched.ShouldBe(2);

		// Shared as the record's own reader shares it: the broker still has the file open for writing.
		using var file = new FileStream(Directory.GetFiles(_directory, "*.jsonl").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var text = new StreamReader(file);
		(await text.ReadToEndAsync(cancellationToken)).ShouldNotContain(mine, Case.Insensitive);
	}

	/// <summary>
	/// Only the failures, or only what names some text in what a listing carries, ignoring case; a broker starting is
	/// neither a failure nor something a search finds.
	/// </summary>
	[Test]
	public async Task Entries_are_read_by_failure_and_by_text(CancellationToken cancellationToken)
	{
		await using var record = Open(out var clock);

		Record(record, "rose_outline", workspace: Solution);
		clock.Advance(TimeSpan.FromMinutes(1));

		using (var call = record.Begin("rose_rename_symbol", null, null, null, "claude-code", null))
		{
			call.Routed(target: "Cart.Total");
			call.Fail("The cart is empty.");
		}

		clock.Advance(TimeSpan.FromMinutes(1));
		Record(record, "rose_debug_events", session: "session-7");

		await record.FlushAsync(cancellationToken);

		var failures = record.Query(new ActivityQuery { Failures = true });
		failures.Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_rename_symbol");
		failures.Matched.ShouldBe(1, "a broker starting failed at nothing");

		record.Query(new ActivityQuery { Text = "CART IS" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_rename_symbol", "by its error");
		record.Query(new ActivityQuery { Text = "cart.total" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_rename_symbol", "by its target");
		record.Query(new ActivityQuery { Text = "Claude" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_rename_symbol", "by its client");
		record.Query(new ActivityQuery { Text = "outline" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_outline", "by its tool");
		record.Query(new ActivityQuery { Text = "shop.slnx" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_outline", "by its workspace");
		record.Query(new ActivityQuery { Text = "session-7" }).Entries.ShouldHaveSingleItem().Tool.ShouldBe("rose_debug_events", "by its debug session");
		record.Query(new ActivityQuery { Text = "nothing like it" }).Entries.ShouldBeEmpty();
		record.Query(new ActivityQuery { Failures = true, Text = "outline" }).Entries.ShouldBeEmpty("both filters hold at once");
	}

	/// <summary>
	/// A listing is read a page at a time by a cursor, newest first, and a call arriving between two reads does not
	/// push the second page back onto what the first already showed.
	/// </summary>
	[Test]
	public async Task A_listing_is_paged_by_a_cursor_that_new_calls_do_not_shift(CancellationToken cancellationToken)
	{
		await using var record = Open(out var clock);

		for (var call = 1; call <= 5; call++)
		{
			Record(record, "rose_outline", workspace: $"{Solution}.{call}");
			clock.Advance(TimeSpan.FromSeconds(1));
		}

		await record.FlushAsync(cancellationToken);

		var first = record.Query(new ActivityQuery { Tool = "rose_outline", Limit = 2 });
		first.Entries.Select(entry => entry.Workspace).ShouldBe([$"{Solution}.5", $"{Solution}.4"]);
		first.Matched.ShouldBe(5);
		first.Next.ShouldNotBeNull();

		Record(record, "rose_outline", workspace: $"{Solution}.6");
		await record.FlushAsync(cancellationToken);

		var second = record.Query(new ActivityQuery { Tool = "rose_outline", Limit = 2, Before = first.Next });
		second.Entries.Select(entry => entry.Workspace).ShouldBe([$"{Solution}.3", $"{Solution}.2"]);
		second.Matched.ShouldBe(3, "what matched from where this page starts");

		var last = record.Query(new ActivityQuery { Tool = "rose_outline", Limit = 2, Before = second.Next });
		last.Entries.ShouldHaveSingleItem().Workspace.ShouldBe($"{Solution}.1");
		last.Next.ShouldBeNull("nothing older matched");

		Should.Throw<ArgumentException>(() => record.Query(new ActivityQuery { Before = "yesterday" }))
			.Message.ShouldContain("is not a cursor the activity record gave", Case.Sensitive);
	}

	/// <summary>A call starting and a call ending each wake whoever waits on the record, which is what answers a window's long poll at once.</summary>
	[Test]
	public async Task A_call_starting_and_ending_each_pulse_the_record()
	{
		await using var record = Open(out _);

		var beforeStart = record.Changed.Next;
		using var call = record.Begin("rose_outline", null, null, null, null, null);
		beforeStart.IsCompleted.ShouldBeTrue("a running call is listed");

		var beforeEnd = record.Changed.Next;
		beforeEnd.IsCompleted.ShouldBeFalse();
		call.Finish(Answer(new { }));
		beforeEnd.IsCompleted.ShouldBeTrue();
	}

	/// <summary>A call is listed while it runs, so a window shows what an agent is doing as well as what it did.</summary>
	[Test]
	public async Task A_running_call_is_listed_as_running_until_it_ends(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);

		using (var call = record.Begin("rose_debug_events", Arguments(("waitSeconds", "30")), null, null, null, null))
		{
			call.Routed(debugSession: "session-1", target: "waiting up to 30s");

			var running = Calls(record, new ActivityQuery()).ShouldHaveSingleItem();
			running.Outcome.ShouldBe(ActivityOutcome.Running);
			running.FinishedUtc.ShouldBeNull();
			running.DebugSession.ShouldBe("session-1");

			record.Find(call.Id).ShouldNotBeNull().Arguments.ShouldNotBeNull();

			call.Finish(Answer(new { events = Array.Empty<string>() }));
		}

		await record.FlushAsync(cancellationToken);

		Calls(record, new ActivityQuery()).ShouldHaveSingleItem().Outcome.ShouldBe(ActivityOutcome.Succeeded);
	}

	/// <summary>
	/// Work a call starts inherits its execution context; once the call ends, what it started can no longer write
	/// into its entry, and a call's routing reaches its own entry and no other.
	/// </summary>
	[Test]
	public async Task The_ambient_call_ends_with_the_call(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		var started = new TaskCompletionSource();
		var ended = new TaskCompletionSource();
		Task? afterwards = null;

		using (var call = record.Begin("rose_workspace_open", null, null, null, null, null))
		{
			afterwards = Task.Run(async () =>
			{
				started.SetResult();
				await ended.Task;

				CallRecording.Current.ShouldBeNull("the call it was started from has ended");
				CallRecording.Current?.Routed(workspace: "elsewhere");
			});

			await started.Task;
			call.Finish(Answer(new { }));
		}

		CallRecording.Current.ShouldBeNull();
		ended.SetResult();
		await afterwards;
		await record.FlushAsync(cancellationToken);

		Calls(record, new ActivityQuery()).ShouldHaveSingleItem().Workspace.ShouldBeNull();
	}

	/// <summary>A file a process died partway through writing is still worth the lines it finished.</summary>
	[Test]
	public async Task A_line_that_does_not_parse_is_stepped_over(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		Record(record, "rose_outline");
		await record.FlushAsync(cancellationToken);

		var torn = Path.Combine(_directory, "20261011-080000-cccccccccccc.jsonl");
		await File.WriteAllTextAsync(torn, "{\"id\":\"torn\",\"ki\n{not json}\n", cancellationToken);

		Calls(record, new ActivityQuery()).ShouldHaveSingleItem().Tool.ShouldBe("rose_outline");
	}

	/// <summary>
	/// A folder the record cannot write to loses entries, counted, and leaves nothing behind; the loop goes on, a flush
	/// still returns, and once the folder can be written to the record writes again.
	/// </summary>
	[Test]
	public async Task A_folder_that_refuses_writes_loses_entries_and_the_record_writes_again_once_it_can(CancellationToken cancellationToken)
	{
		// A file where the folder should be: nothing can be created inside it, on any platform.
		Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
		await File.WriteAllTextAsync(_directory, "in the way", cancellationToken);

		await using var record = Open(out _);
		Record(record, "rose_outline");
		await record.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

		record.Lost.ShouldBe(2, "the broker starting, and the call");
		File.Exists(_directory).ShouldBeTrue("nothing was made in its place");

		File.Delete(_directory);
		Record(record, "rose_diagnostics");
		await record.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

		Calls(record, new ActivityQuery()).ShouldHaveSingleItem().Tool.ShouldBe("rose_diagnostics");
		Directory.GetFiles(_directory, "*.jsonl").ShouldHaveSingleItem();
	}

	/// <summary>
	/// A write that fails partway -- a full disk -- loses its entry, and the next appends to the same file rather than
	/// starting another: a file per failed batch would be an empty file every few seconds for the whole retention. The
	/// line the failure cut short is ended, so the entry after it is still read.
	/// </summary>
	[Test]
	public async Task A_write_that_fails_partway_is_lost_and_the_next_appends_to_the_same_file(CancellationToken cancellationToken)
	{
		var disk = new Disk();
		await using var record = Open(out _, opener: disk.Open);

		Record(record, "rose_outline");
		await record.FlushAsync(cancellationToken);

		disk.Full = true;
		for (var call = 0; call < 3; call++)
		{
			Record(record, "rose_format");
			await record.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
		}

		disk.Full = false;
		Record(record, "rose_diagnostics");
		await record.FlushAsync(cancellationToken);

		record.Lost.ShouldBe(3);
		Directory.GetFiles(_directory, "*.jsonl").ShouldHaveSingleItem();
		Calls(record, new ActivityQuery()).Select(entry => entry.Tool).ShouldBe(["rose_diagnostics", "rose_outline"], ignoreOrder: true);
	}

	/// <summary>A writer stuck on the disk never holds up a call: what does not fit in the queue is dropped and counted.</summary>
	[Test]
	public async Task A_stalled_writer_never_holds_up_a_call_and_counts_what_it_drops(CancellationToken cancellationToken)
	{
		var disk = new Disk { Stalled = true };
		await using var record = Open(out _, opener: disk.Open);
		var clock = System.Diagnostics.Stopwatch.StartNew();

		for (var call = 0; call < ActivityRecord.MaxQueued + 100; call++) Record(record, "rose_outline");

		clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "no call waited for the disk");
		record.Dropped.ShouldBeInRange(100, 101);

		disk.Release();
		await record.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

		record.Lost.ShouldBe(0);
		record.Query(new ActivityQuery { Tool = "rose_outline", Limit = 1 }).Matched.ShouldBe((int)(ActivityRecord.MaxQueued + 100 - record.Dropped));
	}

	/// <summary>Bodies can be tens of kilobytes each, so a page that asks for them is held to what a window shows at once.</summary>
	[Test]
	public async Task A_listing_with_bodies_is_held_to_a_short_page(CancellationToken cancellationToken)
	{
		await using var record = Open(out _);
		for (var call = 0; call < ActivityQuery.MaxLimitWithBodies + 10; call++) Record(record, "rose_outline");
		await record.FlushAsync(cancellationToken);

		var withBodies = record.Query(new ActivityQuery { Tool = "rose_outline", Limit = ActivityQuery.MaxLimit, Bodies = true });
		withBodies.Entries.Count.ShouldBe(ActivityQuery.MaxLimitWithBodies);
		withBodies.Matched.ShouldBe(ActivityQuery.MaxLimitWithBodies + 10);

		record.Query(new ActivityQuery { Tool = "rose_outline", Limit = ActivityQuery.MaxLimit }).Entries.Count.ShouldBe(ActivityQuery.MaxLimitWithBodies + 10);
	}

	/// <summary>
	/// The index holds the newest entries up to its capacity however much the folder holds, says that older ones are
	/// not listed, and still finds one of those by its id; and it keeps an error as an excerpt.
	/// </summary>
	[Test]
	public async Task The_index_keeps_the_newest_entries_up_to_its_capacity(CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_directory);
		var seeded = ActivityRecord.IndexCapacity + 5000;
		var oldest = string.Empty;
		var newest = string.Empty;

		await using (var writer = new StreamWriter(Path.Combine(_directory, "20261001-000000-000000000000.jsonl")))
		{
			for (var index = 0; index < seeded; index++)
			{
				var entry = new ActivityEntry
				{
					Id = ActivityRecord.MintId(),
					Kind = ActivityEntryKind.Call,
					Broker = "seeded",
					StartedUtc = Start - TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(index),
					Tool = "rose_seeded",
					Outcome = ActivityOutcome.Failed,
					Error = index == seeded - 1 ? new string('e', 1000) : "failed",
					Response = "a body",
				};

				if (index == 0) oldest = entry.Id;
				if (index == seeded - 1) newest = entry.Id;

				await writer.WriteLineAsync(JsonSerializer.Serialize(entry, ContractJson.Options).AsMemory(), cancellationToken);
			}
		}

		await using var record = Open(out _);
		await record.FlushAsync(cancellationToken);

		var page = record.Query(new ActivityQuery { Tool = "rose_seeded", Limit = 1 });
		page.Matched.ShouldBeLessThanOrEqualTo(ActivityRecord.IndexCapacity);
		page.Matched.ShouldBeGreaterThan(ActivityRecord.IndexCapacity - 10);
		page.OlderNotListed.ShouldBeTrue();

		var first = page.Entries.ShouldHaveSingleItem();
		first.Id.ShouldBe(newest);
		first.Response.ShouldBeNull();
		first.Error!.Length.ShouldBe(ActivityRecord.ErrorExcerpt);
		first.ErrorFullLength.ShouldBe(1000);

		var found = record.Find(oldest).ShouldNotBeNull("an entry older than the index reaches is still read by its id");
		found.Response.ShouldBe("a body");
		record.Find(newest).ShouldNotBeNull().Error!.Length.ShouldBe(1000);
	}

	private ActivityRecord Open(out SteerableClock clock, TimeSpan? retention = null, Func<string, FileMode, Stream>? opener = null)
	{
		clock = new SteerableClock(Start);
		var options = new BrokerOptions
		{
			TimeProvider = clock,
			ActivityDirectory = _directory,
			ActivityRetention = () => retention ?? TimeSpan.FromDays(7),
		};

		if (opener is not null) options.OpenActivityFile = opener;

		var wrapped = Options.Create(options);
		var relays = new RelayConnections(clock);

		return new ActivityRecord(wrapped, relays, new ConnectedClients(relays, wrapped), NullLogger<ActivityRecord>.Instance);
	}

	private static void Record(
		ActivityRecord record,
		string tool,
		string? relayed = null,
		string? workspace = null,
		string? session = null)
	{
		using var call = record.Begin(tool, null, relayed, null, null, null);

		call.Routed(workspace: workspace, workspaceKey: workspace is null ? null : "Shop-1234", debugSession: session);
		call.Finish(Answer(new { }));
	}

	private static List<ActivityEntry> Calls(ActivityRecord record, ActivityQuery query) =>
		[.. record.Query(query).Entries.Where(entry => entry.Kind == ActivityEntryKind.Call)];

	private static CallToolResult Answer(object structured)
	{
		var element = JsonSerializer.SerializeToElement(structured);

		return new CallToolResult { StructuredContent = element, Content = [new TextContentBlock { Text = element.GetRawText() }] };
	}

	private static Dictionary<string, JsonElement> Arguments(params (string Name, string Value)[] arguments) =>
		arguments.ToDictionary(argument => argument.Name, argument => JsonSerializer.SerializeToElement(argument.Value));

	private sealed class SteerableClock(DateTimeOffset start) : TimeProvider
	{
		private DateTimeOffset _now = start;

		public void Advance(TimeSpan by) => _now += by;

		public override DateTimeOffset GetUtcNow() => _now;
	}

	/// <summary>
	/// The record's files as a disk that can fill up -- a write puts down half its bytes and fails -- or stall until it
	/// is released, which no folder can be made to do on demand.
	/// </summary>
	private sealed class Disk
	{
		private readonly ManualResetEventSlim _released = new(initialState: false);

		public volatile bool Full;

		public volatile bool Stalled;

		public Stream Open(string path, FileMode mode) =>
			new Faulty(new FileStream(path, mode, FileAccess.Write, FileShare.Read | FileShare.Delete), this);

		public void Release()
		{
			Stalled = false;
			_released.Set();
		}

		private void Writing()
		{
			if (Stalled) _released.Wait();
		}

		private sealed class Faulty(FileStream inner, Disk disk) : Stream
		{
			public override bool CanRead => false;

			public override bool CanSeek => inner.CanSeek;

			public override bool CanWrite => true;

			public override long Length => inner.Length;

			public override long Position
			{
				get => inner.Position;
				set => inner.Position = value;
			}

			public override void Write(ReadOnlySpan<byte> buffer)
			{
				disk.Writing();

				if (disk.Full)
				{
					inner.Write(buffer[..(buffer.Length / 2)]);
					inner.Flush();
					throw new IOException("There is not enough space on the disk.");
				}

				inner.Write(buffer);
			}

			public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

			public override void WriteByte(byte value) => Write([value]);

			public override void Flush() => inner.Flush();

			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

			public override void SetLength(long value) => inner.SetLength(value);

			protected override void Dispose(bool disposing)
			{
				if (disposing) inner.Dispose();

				base.Dispose(disposing);
			}
		}
	}
}
