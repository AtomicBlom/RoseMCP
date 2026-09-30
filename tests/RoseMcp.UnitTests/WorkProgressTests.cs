using ModelContextProtocol;

using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// The scale arithmetic, which is the part that decides whether a progress bar is worth looking at
/// or worth distrusting.
/// </summary>
public sealed class WorkProgressTests
{
	[Test]
	public void The_work_phase_carries_on_from_where_the_wait_ended()
	{
		var sink = new RecordingSink();
		var (waiting, working) = WorkProgress.Split(sink);

		// Four fifths of the way through a wait that may claim at most half the bar.
		waiting.Report("Loading", 80);
		working.Report("Analysing", 50);

		sink.Values[0].Progress.ShouldBe(40, 0.0005);
		sink.Values[1].Progress.ShouldBe(70, 0.0005);
	}

	/// <summary>
	/// A call that finds the workspace warm waited for nothing, so its own work is all there is to
	/// report and it gets the whole bar rather than starting at an arbitrary halfway mark.
	/// </summary>
	[Test]
	public void A_call_that_never_waited_gets_the_whole_scale()
	{
		var sink = new RecordingSink();
		var (_, working) = WorkProgress.Split(sink);

		working.Report("Analysing", 50);

		sink.Values[0].Progress.ShouldBe(50, 0.0005);
	}

	/// <summary>
	/// No total is the protocol's way of saying the sender does not know how much work there is.
	/// The number itself must not go backwards even so, since progress is only ever allowed to rise.
	/// </summary>
	[Test]
	public void A_report_with_no_percentage_keeps_the_number_but_drops_the_total()
	{
		var sink = new RecordingSink();
		var progress = WorkProgress.For(sink);

		progress.Report("Loading", 30);
		progress.Report("Searching the solution");

		sink.Values[0].Total.ShouldBe(100);
		sink.Values[1].Total.ShouldBeNull();
		sink.Values[1].Progress.ShouldBe(30, 0.0005);
	}

	[Test]
	public void A_slice_maps_an_operation_onto_its_share_of_the_caller_scale()
	{
		var captured = new CapturingProgress();

		captured.Slice(20, 60)!.Report("halfway", 50);

		var report = captured.Reports.ShouldHaveSingleItem();

		report.Percent.ShouldBe(40);
	}

	[Test]
	public void Slicing_nothing_is_still_nothing()
	{
		IWorkProgress? nobody = null;

		nobody.Slice(0, 50).ShouldBeNull();
	}

	/// <summary>
	/// A call that arrives halfway through a load must be told what it is waiting for, rather than
	/// showing nothing until the next project happens to finish.
	/// </summary>
	[Test]
	public void Shared_work_catches_a_late_listener_up()
	{
		var shared = new SharedWorkProgress();
		using var operation = shared.Begin("Loading Thing.sln");

		shared.Report("Loaded Core (1/2)", 30);

		var listener = new CapturingProgress();
		using var following = shared.Follow(listener);

		var caught = listener.Reports.ShouldHaveSingleItem();

		caught.Message.ShouldBe("Loaded Core (1/2)");
		caught.Percent.ShouldBe(30);
	}

	[Test]
	public void Shared_work_has_nothing_to_say_once_it_is_over()
	{
		var shared = new SharedWorkProgress();
		shared.Begin("Loading Thing.sln").Dispose();

		var listener = new CapturingProgress();
		using var following = shared.Follow(listener);

		// An hour later, a call must not be told about the load it missed.
		listener.Reports.ShouldBeEmpty();
	}

	[Test]
	public void Shared_work_stops_reporting_to_a_listener_that_has_let_go()
	{
		var shared = new SharedWorkProgress();
		var listener = new CapturingProgress();

		shared.Follow(listener).Dispose();
		shared.Report("Reloading the solution", 10);

		listener.Reports.ShouldBeEmpty();
	}

	private sealed class RecordingSink : IProgress<ProgressNotificationValue>
	{
		private readonly List<ProgressNotificationValue> _values = [];

		public IReadOnlyList<ProgressNotificationValue> Values
		{
			get
			{
				lock (_values)
				{
					return [.. _values];
				}
			}
		}

		public void Report(ProgressNotificationValue value)
		{
			lock (_values)
			{
				_values.Add(value);
			}
		}
	}

	/// <summary>
	/// A percentage going down is legitimate here, and a listener has to see it.
	/// <para>
	/// Shared work deliberately fans a reload's own scale into calls already in flight: a call sitting
	/// at 75% of its own operation really is now waiting on a reload that has just started, and saying
	/// 0 is the honest answer to "why is this taking so long". Clamping to the highest value seen
	/// would pin that bar at whatever the last operation reached and leave it there for the rest of the
	/// reload -- which is the fix that suggests itself for the out-of-order progress an SSE transport
	/// produces, and is why this is asserted rather than left to be rediscovered.
	/// </para>
	/// </summary>
	[Test]
	public void Shared_work_passes_on_a_reset_to_a_new_operation()
	{
		var shared = new SharedWorkProgress();
		var listener = new CapturingProgress();

		using (shared.Begin("Loading Thing.sln"))
		{
			using var following = shared.Follow(listener);
			shared.Report("Loaded Core (3/4)", 75);

			// A reload starting under a call that was already three-quarters through its own work.
			using var reloading = shared.Begin("Reloading the solution");
			shared.Report("Loaded Core (1/4)", 25);
		}

		var percentages = listener.Reports.Select(report => report.Percent).ToList();

		percentages.ShouldContain(75d);
		percentages.ShouldContain(25d);
		percentages.IndexOf(75d).ShouldBeLessThan(percentages.IndexOf(25d),
			"the reload's own scale must reach a listener that has already seen a higher percentage");
	}
}
