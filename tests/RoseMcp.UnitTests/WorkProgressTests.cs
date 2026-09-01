using ModelContextProtocol;

using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// The scale arithmetic, which is the part that decides whether a progress bar is worth looking at
/// or worth distrusting.
/// </summary>
public sealed class WorkProgressTests
{
	[Fact]
	public void The_work_phase_carries_on_from_where_the_wait_ended()
	{
		var sink = new RecordingSink();
		var (waiting, working) = WorkProgress.Split(sink);

		// Four fifths of the way through a wait that may claim at most half the bar.
		waiting.Report("Loading", 80);
		working.Report("Analysing", 50);

		Assert.Equal(40, sink.Values[0].Progress, 3);
		Assert.Equal(70, sink.Values[1].Progress, 3);
	}

	/// <summary>
	/// A call that finds the workspace warm waited for nothing, so its own work is all there is to
	/// report and it gets the whole bar rather than starting at an arbitrary halfway mark.
	/// </summary>
	[Fact]
	public void A_call_that_never_waited_gets_the_whole_scale()
	{
		var sink = new RecordingSink();
		var (_, working) = WorkProgress.Split(sink);

		working.Report("Analysing", 50);

		Assert.Equal(50, sink.Values[0].Progress, 3);
	}

	/// <summary>
	/// No total is the protocol's way of saying the sender does not know how much work there is.
	/// The number itself must not go backwards even so, since progress is only ever allowed to rise.
	/// </summary>
	[Fact]
	public void A_report_with_no_percentage_keeps_the_number_but_drops_the_total()
	{
		var sink = new RecordingSink();
		var progress = WorkProgress.For(sink);

		progress.Report("Loading", 30);
		progress.Report("Searching the solution");

		Assert.Equal(100, sink.Values[0].Total);
		Assert.Null(sink.Values[1].Total);
		Assert.Equal(30, sink.Values[1].Progress, 3);
	}

	[Fact]
	public void A_slice_maps_an_operation_onto_its_share_of_the_caller_scale()
	{
		var captured = new CapturingProgress();

		captured.Slice(20, 60)!.Report("halfway", 50);

		var report = Assert.Single(captured.Reports);

		Assert.Equal(40, report.Percent);
	}

	[Fact]
	public void Slicing_nothing_is_still_nothing()
	{
		IWorkProgress? nobody = null;

		Assert.Null(nobody.Slice(0, 50));
	}

	/// <summary>
	/// A call that arrives halfway through a load must be told what it is waiting for, rather than
	/// showing nothing until the next project happens to finish.
	/// </summary>
	[Fact]
	public void Shared_work_catches_a_late_listener_up()
	{
		var shared = new SharedWorkProgress();
		using var operation = shared.Begin("Loading Thing.sln");

		shared.Report("Loaded Core (1/2)", 30);

		var listener = new CapturingProgress();
		using var following = shared.Follow(listener);

		var caught = Assert.Single(listener.Reports);

		Assert.Equal("Loaded Core (1/2)", caught.Message);
		Assert.Equal(30, caught.Percent);
	}

	[Fact]
	public void Shared_work_has_nothing_to_say_once_it_is_over()
	{
		var shared = new SharedWorkProgress();
		shared.Begin("Loading Thing.sln").Dispose();

		var listener = new CapturingProgress();
		using var following = shared.Follow(listener);

		// An hour later, a call must not be told about the load it missed.
		Assert.Empty(listener.Reports);
	}

	[Fact]
	public void Shared_work_stops_reporting_to_a_listener_that_has_let_go()
	{
		var shared = new SharedWorkProgress();
		var listener = new CapturingProgress();

		shared.Follow(listener).Dispose();
		shared.Report("Reloading the solution", 10);

		Assert.Empty(listener.Reports);
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
}
