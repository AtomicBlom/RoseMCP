using ModelContextProtocol;

namespace RoseMcp.Worker;

/// <summary>Adapters between operations that report progress and whoever wants to hear it.</summary>
public static class WorkProgress
{
	/// <summary>
	/// The most of a call's scale the wait for a workspace may consume, leaving the rest for the
	/// work the caller actually asked for.
	/// </summary>
	private const double WaitingCap = 50;

	/// <summary>
	/// Reports to an MCP client as progress notifications against the call in flight. Sending them
	/// requires a request to attach to, which is why shared work goes through
	/// <see cref="SharedWorkProgress"/> instead of straight here.
	/// </summary>
	public static IWorkProgress For(IProgress<ProgressNotificationValue> sink) => new McpWorkProgress(sink);

	/// <summary>
	/// Splits one call's scale into the wait for a workspace snapshot and the work done with it.
	/// <para>
	/// Every tool has these two phases, and on a cold start the wait is much the larger. The work
	/// phase picks up wherever the wait left off rather than at a fixed mark, so a call that found
	/// the workspace already warm gets the whole bar for its own work, and one that waited through
	/// a load carries on from there instead of jumping.
	/// </para>
	/// </summary>
	public static (IWorkProgress Waiting, IWorkProgress Working) Split(IProgress<ProgressNotificationValue> sink)
	{
		var call = new PhasedCall(For(sink));

		return (call.Waiting, call.Working);
	}

	/// <summary>
	/// A view of <paramref name="progress"/> where an operation's own 0 to 100 lands between
	/// <paramref name="from"/> and <paramref name="to"/> on the caller's scale. Null in, null out,
	/// so a caller with nobody listening needs no special case.
	/// </summary>
	public static IWorkProgress? Slice(this IWorkProgress? progress, double from, double to) =>
		progress is null ? null : new SlicedWorkProgress(progress, from, to);

	private sealed class McpWorkProgress(IProgress<ProgressNotificationValue> sink) : IWorkProgress
	{
		private double _lastPercent;

		public void Report(string message, double? percentComplete)
		{
			if (percentComplete is { } percent) _lastPercent = percent;

			sink.Report(new ProgressNotificationValue
			{
				Progress = (float)_lastPercent,

				// An absent total is how the protocol says "no idea how much work there is". Sending
				// a total alongside a percentage we do not have would claim the last real report
				// still describes where we are.
				Total = percentComplete is null ? null : 100,
				Message = message,
			});
		}
	}

	private sealed class SlicedWorkProgress(IWorkProgress inner, double from, double to) : IWorkProgress
	{
		public void Report(string message, double? percentComplete) => inner.Report(
			message,
			percentComplete is { } percent ? from + ((to - from) * Math.Clamp(percent, 0, 100) / 100) : null);
	}

	/// <summary>
	/// One call's two phases over a single scale, where the second starts from the high-water mark
	/// of the first.
	/// </summary>
	private sealed class PhasedCall(IWorkProgress inner)
	{
		private double _waitedTo;

		public IWorkProgress Waiting => new Phase(this, waiting: true);

		public IWorkProgress Working => new Phase(this, waiting: false);

		private void Report(bool waiting, string message, double? percentComplete)
		{
			if (percentComplete is not { } percent)
			{
				inner.Report(message);
				return;
			}

			var clamped = Math.Clamp(percent, 0, 100);

			if (waiting)
			{
				_waitedTo = clamped * WaitingCap / 100;
				inner.Report(message, _waitedTo);
				return;
			}

			inner.Report(message, _waitedTo + ((100 - _waitedTo) * clamped / 100));
		}

		private sealed class Phase(PhasedCall call, bool waiting) : IWorkProgress
		{
			public void Report(string message, double? percentComplete) =>
				call.Report(waiting, message, percentComplete);
		}
	}
}
