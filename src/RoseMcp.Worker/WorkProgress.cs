using ModelContextProtocol;

namespace RoseMcp.Worker;

/// <summary>
/// Where a long operation says how far it has got.
/// <para>
/// A percentage is of the reporting operation itself, never of the request as a whole, and a caller
/// that spans several operations hands each one a slice of its own scale. That is what keeps a
/// progress bar moving in one direction when one phase ends and the next begins.
/// </para>
/// <para>
/// A null percentage means the operation genuinely does not know how far along it is, which is not
/// the same as being at zero. Finding references has no idea up front how much of a solution it
/// will have to look at, and saying so beats a bar that sits at nothing and reads as a hang.
/// </para>
/// </summary>
public interface IWorkProgress
{
	void Report(string message, double? percentComplete = null);
}

/// <summary>Adapters between operations that report progress and whoever wants to hear it.</summary>
public static class WorkProgress
{
	/// <summary>
	/// Reports to an MCP client as progress notifications against the call in flight. Sending them
	/// requires a request to attach to, which is why shared work goes through
	/// <see cref="SharedWorkProgress"/> instead of straight here.
	/// </summary>
	public static IWorkProgress For(IProgress<ProgressNotificationValue> sink) => new McpWorkProgress(sink);

	/// <summary>
	/// Splits one call's scale into the wait for a workspace snapshot and the work done with it.
	/// <para>
	/// Every tool has these two phases, and waiting for a load or a reload is frequently the larger
	/// of them. Giving each phase half the scale is what stops a bar from starting over when the
	/// wait ends -- and a call that arrives to find the workspace already warm simply begins at the
	/// halfway mark, which is a fair account of what it skipped.
	/// </para>
	/// </summary>
	public static (IWorkProgress Waiting, IWorkProgress Working) Split(IProgress<ProgressNotificationValue> sink)
	{
		var work = For(sink);

		return (new SlicedWorkProgress(work, 0, 50), new SlicedWorkProgress(work, 50, 100));
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
}
