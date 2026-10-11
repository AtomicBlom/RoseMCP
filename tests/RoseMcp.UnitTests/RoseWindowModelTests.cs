using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// The parts of the RoseMCP window that are not a row: the memory bar, the build under the view list, the Activity
/// view's list and filters, what the window says when Rose cannot be reached, and the command that opens it.
/// </summary>
public sealed class RoseWindowModelTests
{
	private const long Megabyte = 1024 * 1024;

	private static readonly DateTimeOffset Noon = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// Rose's total is the broker, its workers and its live-app hosts; a debugged app is listed apart from it, since
	/// an app under a debugger holds what it would hold anyway; the machine's total sits at the end.
	/// </summary>
	[Test]
	public void The_memory_bar_keeps_debugged_apps_out_of_roses_total()
	{
		var report = new MemoryReport
		{
			SampledUtc = Noon.UtcDateTime,
			Broker = Process(1, 380),
			Workers =
			[
				new WorkerMemory { Workspace = @"D:\a\A.slnx", WorkspaceKey = "A-1", Process = Process(2, 2048) },
				new WorkerMemory { Workspace = @"D:\b\B.slnx", WorkspaceKey = "B-2", Process = Process(3, 1024) },
				new WorkerMemory { Workspace = @"D:\c\C.slnx", WorkspaceKey = "C-3", Process = null },
			],
			LiveApps =
			[
				new LiveAppMemory { SessionId = "s1", TargetDescription = "Drawboard PDF", Host = Process(4, 120), Target = Process(5, 1100) },
			],
			Machine = new MachineMemory { TotalBytes = 32L * 1024 * Megabyte, AvailableBytes = 10L * 1024 * Megabyte },
		};

		var bar = MemorySummary.Of(report);

		bar.Rose.ShouldBe("3.5 GB", "380 MB + 3 GB of workers + 120 MB of host, and not the debugged app");
		bar.Split.ShouldBe("broker 380 MB  ·  2 workers 3.0 GB  ·  1 live-app host 120 MB");
		bar.Debugged.ShouldBe("Debugged: Drawboard PDF 1.1 GB");
		bar.Machine.ShouldBe("Machine: 22 of 32 GB in use");
		bar.Detail.ShouldContain("A.slnx (A-1)");
		bar.Detail.ShouldContain("C.slnx (C-3): not running");
		bar.Detail.ShouldContain("managed heap");
	}

	/// <summary>
	/// The build says its version, commit, when and where it was built, and how far its checkout has moved on; and a
	/// window that is another build from the broker says so.
	/// </summary>
	[Test]
	public void The_build_line_says_which_build_and_how_far_its_checkout_moved()
	{
		var commit = new string('c', 40);
		var hello = new OperatorHello
		{
			Build = new BuildIdentity { Version = "1.3.0-rc.1.4", Commit = commit, BuiltUtc = Noon.AddHours(-2), Checkout = @"D:\Personal\RoseMCP" },
			Checkout = new CheckoutDistance
			{
				Checkout = @"D:\Personal\RoseMCP",
				Head = new CommitsPast { Ref = "HEAD", Count = 2 },
				OriginMain = new CommitsPast { Ref = "origin/main", Unknown = "no remote called origin" },
				CheckedUtc = Noon,
			},
		};

		var same = BuildLine.Of(hello, new BuildIdentity { Version = "1.3.0-rc.1.4", Commit = commit }, Noon);

		same.Build.ShouldBe(@"1.3.0-rc.1.4 ccccccc built 2h ago from D:\Personal\RoseMCP");
		same.CheckoutIsAhead.ShouldBeTrue();
		same.Checkout.ShouldStartWith("The checkout is 2 commits ahead; origin/main: no remote called origin");
		same.HasMismatch.ShouldBeFalse();

		var other = BuildLine.Of(hello, new BuildIdentity { Version = "1.3.0-rc.1.4", Commit = new string('d', 40) }, Noon);
		other.Mismatch.ShouldContain("The broker is build");
	}

	/// <summary>
	/// The Activity view merges the long-polled newest page with older pages read on request, keeps an older row open
	/// as new calls arrive, and forgets the older pages when the question changes.
	/// </summary>
	[Test]
	public void The_activity_list_keeps_older_pages_until_the_question_changes()
	{
		var list = new ActivityList();
		var older = OperationRowTests.Call("old", Noon.AddHours(-3), Noon.AddHours(-3));
		var newest = OperationRowTests.Call("new", Noon.AddMinutes(-1), Noon);

		list.IsAnswered.ShouldBeFalse("nothing has been read, which is not the same as nothing matching");
		list.ShowNewest(Page([newest], next: "cursor-1", matched: 2), Noon);
		list.IsAnswered.ShouldBeTrue();
		list.HasOlder.ShouldBeTrue();
		list.Header.ShouldBe("2 entries match  ·  kept for 7 days");

		list.ShowOlder(Page([older], next: null, matched: 1), Noon);
		list.Rows.Select(row => row.Id).ShouldBe(["new", "old"]);
		list.HasOlder.ShouldBeFalse();
		list.Footer.ShouldContain("everything the record holds");

		var arriving = OperationRowTests.Call("arriving", Noon.AddSeconds(-1));
		list.ShowNewest(Page([arriving, newest], next: "cursor-2", matched: 3), Noon);
		list.Rows.Select(row => row.Id).ShouldBe(["arriving", "new", "old"], "the older page stays while the newest moves on");

		list.Ask(new ActivityFilter { Failures = true });
		list.Rows.ShouldBeEmpty();
		list.IsAnswered.ShouldBeFalse("the new question has no answer yet");
		list.Filter.Failures.ShouldBeTrue();
		list.Filter.Limit.ShouldBe(ActivityList.PageSize);
	}

	/// <summary>The agent filter offers each agent the record names once, by the key that outlives a restart, newest first.</summary>
	[Test]
	public void The_agent_filter_offers_each_agent_once()
	{
		var entries = new[]
		{
			OperationRowTests.Call("1", Noon.AddMinutes(-9), Noon) with { ClientKey = "k-a", ClientId = "row-1", ClientLabel = "ink" },
			OperationRowTests.Call("2", Noon.AddMinutes(-5), Noon) with { ClientKey = "k-a", ClientId = "row-7", ClientLabel = "ink" },
			OperationRowTests.Call("3", Noon.AddMinutes(-1), Noon) with { ClientKey = "k-b", Origin = @"D:\work\activity-log" },
			OperationRowTests.Started("restart", Noon.AddMinutes(-7)),
		};

		FilterChoice.Agents(entries).ShouldBe(
		[
			new FilterChoice("Any agent", null),
			new FilterChoice("activity-log", "k-b"),
			new FilterChoice("ink", "k-a"),
		]);
	}

	/// <summary>
	/// What the window says when Rose cannot be reached says what will put it right: a refused token after a restart
	/// is the tray's icon, a broker that is not there is waited for.
	/// </summary>
	[Test]
	public void Not_serving_says_what_puts_it_right()
	{
		var broker = new Uri("http://127.0.0.1:5077");

		RoseText.Problem(new OperatorException(OperatorFailure.Unauthorized, "refused", null), hasToken: true, broker)
			.ShouldContain("restarted since this window was opened");
		RoseText.Problem(new OperatorException(OperatorFailure.Unauthorized, "refused", null), hasToken: false, broker)
			.ShouldContain("without Rose's operator token");
		RoseText.Problem(new OperatorException(OperatorFailure.Unreachable, "gone", null), hasToken: true, broker)
			.ShouldContain("not serving at http://127.0.0.1:5077");
		RoseText.Problem(new OperatorException(OperatorFailure.Failed, "The broker said so.", null), hasToken: true, broker)
			.ShouldBe("The broker said so.");
	}

	/// <summary>The command a person copies opens this window against this broker, and reads back as the same options.</summary>
	[Test]
	public void The_copied_command_reads_back_as_the_same_options()
	{
		var options = InspectorOptions.Parse(["--port", "5079", "--token", "tok-1"]);

		var command = options.CommandLine(@"C:\Program Files\Rose\app\RoseMcp.App.exe");

		command.ShouldStartWith("\"C:\\Program Files\\Rose\\app\\RoseMcp.App.exe\"");
		InspectorOptions.Parse(CommandLine.Split(command)).ShouldBe(options);
	}

	private static ProcessMemory Process(int id, long megabytes) => new()
	{
		ProcessId = id,
		WorkingSetBytes = megabytes * Megabyte,
		PrivateMemoryBytes = megabytes * Megabyte,
		ManagedHeapBytes = 10 * Megabyte,
	};

	private static ActivityPage Page(IReadOnlyList<ActivityEntry> entries, string? next, int matched) => new()
	{
		Entries = entries,
		Bodies = false,
		Matched = matched,
		Next = next,
		Retention = TimeSpan.FromDays(7),
		Version = Guid.NewGuid().ToString("N"),
	};
}
