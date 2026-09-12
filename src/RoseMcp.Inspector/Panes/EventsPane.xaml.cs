using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Panes;

/// <summary>
/// The session's event tail, filtered, with each stop's captured frames and variables behind an
/// expander.
/// <para>
/// It primes with a read that waits for nothing and then long-polls, which is what makes the tail
/// arrive as things happen rather than a second late: the host holds the request open until an
/// event lands, so there is no interval to miss things in.
/// </para>
/// </summary>
public sealed partial class EventsPane : UserControl
{
	/// <summary>
	/// How long a poll asks the host to hold. Long enough that an idle target costs two requests a
	/// minute, short enough that a tray restart is noticed while somebody is still looking.
	/// </summary>
	private const int WaitSeconds = 30;

	/// <summary>How much tail one read may bring back. A burst of a thousand events still pages.</summary>
	private const int PageLimit = 500;

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private PollLoop? _poll;
	private InspectedSession? _session;
	private IReadOnlyList<string>? _kinds;
	private bool _primed;

	public EventsPane()
	{
		InitializeComponent();
		ShowFilter(null, "everything");
	}

	/// <summary>Gives the pane the client it reads through and somewhere to report a failure.</summary>
	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
		_poll = new PollLoop(ReadAsync, TimeSpan.Zero, exception => _report?.Invoke(exception));
	}

	/// <summary>
	/// Points the pane at a session's tail. The tail is the session's rather than the pane's, so
	/// switching away and back does not lose what was collected or re-read it.
	/// </summary>
	public void Bind(InspectedSession session)
	{
		_session = session;
		_primed = false;
		Tail.ItemsSource = session.Events;
		ShowState();
	}

	/// <summary>
	/// Whether the pane is the one on screen. A pane nobody can see must not be holding a long poll
	/// open on the reader's behalf -- it is a request per pane per session otherwise, all of them
	/// answering questions nothing is going to render.
	/// </summary>
	public void Showing(bool visible)
	{
		if (visible && _session is not null) _poll?.Start();
		else _poll?.Stop();
	}

	/// <summary>
	/// One read. The first waits for nothing, so a pane that opens onto a session with a tail
	/// already buffered fills immediately rather than after the first long poll answers.
	/// </summary>
	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		if (_client is not { } client || _session is not { } session) return;

		var wait = _primed ? WaitSeconds : 0;
		var page = await client.EventsAsync(session.SessionId, session.Cursor, _kinds, PageLimit, wait, cancellationToken);

		_primed = true;

		var wasAtBottom = AtBottom();
		session.Absorb(page);
		ShowState();

		// Only when they were already there. Scrolling a reader who had gone back to look at
		// something is the surest way to make a live tail unusable.
		if (wasAtBottom) StickToBottom();
	}

	private void ShowState()
	{
		var session = _session;

		DroppedBar.IsOpen = session?.HasNotice == true;
		DroppedBar.Message = session?.Notice ?? string.Empty;

		var empty = session is null || !session.HasEvents;
		Empty.Text = empty ? InspectorText.NoEvents : string.Empty;
		Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
		Scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
	}

	private bool AtBottom() =>
		Scroll.ScrollableHeight <= 0 || Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24;

	/// <summary>
	/// How many times a scroll to the bottom is retried. The repeater realises rows as they come
	/// into view, so each pass discovers more extent than the last and one pass lands short --
	/// by more the bigger the page that arrived. Three converges on every page this reads.
	/// </summary>
	private const int StickPasses = 3;

	private void StickToBottom(int pass = 0)
	{
		// After the current layout pass, because the rows just added have no height until the
		// repeater has arranged them: a ChangeView issued now scrolls to where the bottom was
		// before they arrived, which on a first read is the top of the list.
		DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
		{
			Tail.UpdateLayout();
			Scroll.UpdateLayout();

			// Past the end rather than to the measured end, and ChangeView clamps to whatever the
			// extent turns out to be.
			Scroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);

			// Unconditionally, rather than until AtBottom says so: the extent it would be asked
			// about is the under-measured one that made the first pass land short.
			if (pass + 1 < StickPasses) StickToBottom(pass + 1);
		});
	}

	private void OnFilterAll(object sender, RoutedEventArgs args) => ShowFilter(null, "everything");

	private void OnFilterStops(object sender, RoutedEventArgs args) => ShowFilter(
		[nameof(LiveDebugEventKind.BreakpointHit), nameof(LiveDebugEventKind.StepComplete), nameof(LiveDebugEventKind.Paused)],
		"stops only");

	private void OnFilterExceptions(object sender, RoutedEventArgs args) => ShowFilter(
		[nameof(LiveDebugEventKind.ExceptionFirstChance), nameof(LiveDebugEventKind.ExceptionUnhandled)],
		"exceptions only");

	private void OnFilterLogs(object sender, RoutedEventArgs args) => ShowFilter(
		[nameof(LiveDebugEventKind.LogMessage)],
		"log output only");

	/// <summary>
	/// Changes what the tail asks for from here on. What has already been collected stays: the
	/// filter is applied by the host as events arrive, not to the list, so re-reading the tail to
	/// apply it would mean asking the host for events it may no longer have.
	/// </summary>
	private void ShowFilter(IReadOnlyList<string>? kinds, string note)
	{
		_kinds = kinds;
		FilterNote.Text = $"showing {note} from here on";

		// Immediately rather than at the next interval: a reader who just clicked a chip is
		// watching for the effect of it.
		_poll?.Kick();
	}
}
