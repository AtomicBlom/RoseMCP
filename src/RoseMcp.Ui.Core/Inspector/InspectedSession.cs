using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Everything the inspector holds about the one session it is showing: the session stream, the breakpoints and
/// tracepoints, and the visual tree.
/// <para>
/// Per session rather than per pane, so a pane rebound to the same session does not throw away a stream that took
/// minutes to accumulate -- and so the stream's cursor survives, which is what stops a rebind re-reading everything
/// the host still has buffered.
/// </para>
/// </summary>
public sealed class InspectedSession : Observable
{
	public InspectedSession(string sessionId)
	{
		SessionId = sessionId;
		Stream = new SessionStream(sessionId);
	}

	public string SessionId { get; }

	/// <summary>The app's events, the agents' calls into the session and the person's notes, in the order they happened.</summary>
	public SessionStream Stream { get; }

	public ObservableCollection<BreakpointRow> Breakpoints { get; } = [];

	public ObservableCollection<TracepointRow> Tracepoints { get; } = [];

	/// <summary>
	/// The live app's visual tree, the element selected in it, and that element's properties.
	/// <para>
	/// Per session rather than per pane, for the reason the stream is: a handle is stable for the life of the element,
	/// so the expansion and the selection somebody built up are worth keeping across anything that rebinds the pane. A
	/// target with no XAML simply never has it read.
	/// </para>
	/// </summary>
	public XamlInspection Xaml { get; } = new();

	/// <summary>Merges the host's breakpoints in place, so a row a reader is about to click stays put.</summary>
	public void Absorb(LiveBreakpointList list) => Rows.Merge(
		Breakpoints,
		list.Breakpoints,
		row => row.Id,
		breakpoint => breakpoint.Id,
		breakpoint => new BreakpointRow(breakpoint),
		(row, breakpoint) => row.Update(breakpoint));

	/// <summary>The same for tracepoints.</summary>
	public void Absorb(LiveTracepointList list) => Rows.Merge(
		Tracepoints,
		list.Tracepoints,
		row => row.Id,
		tracepoint => tracepoint.Id,
		tracepoint => new TracepointRow(tracepoint),
		(row, tracepoint) => row.Update(tracepoint));
}
