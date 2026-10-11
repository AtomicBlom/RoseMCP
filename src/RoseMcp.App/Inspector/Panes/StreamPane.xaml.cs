using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Inspector.Panes;

/// <summary>
/// The session stream, beside the code: the app's events, the agents' calls into this session, and the person's notes
/// with the answers to them, in the order they happened.
/// <para>
/// Three long polls feed it, each answered as something arrives rather than on an interval: the host's event tail
/// from its cursor, the activity record narrowed to this session, and the notes. All three start when the pane is
/// bound to a live session and stop -- cancelling what each holds open -- when the session ends, when another is
/// bound, and when the window closes. They resume on the window's thread, so what they bring is merged into bound
/// rows directly.
/// </para>
/// </summary>
public sealed partial class StreamPane : UserControl
{
	/// <summary>How much of the tail one read may bring back. A burst of a thousand events still pages.</summary>
	private const int EventPage = 500;

	/// <summary>How many of the session's newest calls a read of the record brings. More than any burst between two reads.</summary>
	private const int CallPage = 200;

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private Action<NoteSubject, FrameworkElement>? _ask;
	private Action<NoteSubject?>? _regard;

	private SessionStream? _stream;
	private ViewWatch<LiveDebugEventPage>? _events;
	private ViewWatch<ActivityPage>? _calls;
	private ViewWatch<NoteList>? _notes;

	public StreamPane() => InitializeComponent();

	public void Attach(
		OperatorClient client,
		Action<Exception> report,
		Action<NoteSubject, FrameworkElement> ask,
		Action<NoteSubject?> regard)
	{
		_client = client;
		_report = report;
		_ask = ask;
		_regard = regard;
	}

	/// <summary>
	/// Points the pane at a session's stream and starts reading it. The stream is the session's, so the cursor and what
	/// was collected survive a rebind to the same one.
	/// </summary>
	public void Bind(InspectedSession session)
	{
		Stop();

		if (_client is not { } client) return;

		var stream = session.Stream;
		_stream = stream;
		stream.Filter = FilterOf(FilterChoice.SelectedItem);
		StreamRows.ItemsSource = stream.Rows;

		var filter = new ActivityFilter { Session = session.SessionId, Limit = CallPage };
		_calls = new ViewWatch<ActivityPage>(
			(after, wait, cancellationToken) => client.ActivityAsync(filter, before: null, after, wait, cancellationToken),
			page => page.Version,
			page => Took(() => stream.Absorb(page)),
			Failed);
		_notes = new ViewWatch<NoteList>(
			(after, wait, cancellationToken) => client.NotesAsync(after, wait, cancellationToken),
			notes => notes.Version,
			notes => Took(() => stream.Absorb(notes)),
			Failed);
		_events = stream.Tail(
			(after, wait, cancellationToken) => client.EventsAsync(stream.SessionId, after, kinds: null, EventPage, wait, cancellationToken),
			page => Took(() => stream.Absorb(page)),
			Failed);

		_events.Start();
		_calls.Start();
		_notes.Start();

		ShowState();
	}

	/// <summary>
	/// Stops every read, cancelling what each holds open: for a session that has ended, which has nothing more to say,
	/// and for a window that is closing. What was collected stays on screen.
	/// </summary>
	public void Stop()
	{
		_events?.Stop();
		_calls?.Stop();
		_notes?.Stop();
		_events = null;
		_calls = null;
		_notes = null;
	}

	/// <summary>
	/// Stops every read, then reads what is left of the tail once, waiting for nothing, for a session that has ended:
	/// the event saying the app exited can be in flight when the session poll sees the end first, and stopping the long
	/// poll cancels that read.
	/// </summary>
	/// <returns>When the app's session recorded it exiting, or null where it recorded no exit.</returns>
	public async Task<DateTimeOffset?> FinishAsync(CancellationToken cancellationToken)
	{
		Stop();

		if (_client is not { } client || _stream is not { } stream) return null;
		if (stream.ExitedUtc is { } already) return already;

		try
		{
			var page = await client.EventsAsync(stream.SessionId, stream.Cursor, kinds: null, EventPage, waitSeconds: 0, cancellationToken);
			if (ReferenceEquals(stream, _stream)) Took(() => stream.Absorb(page));
		}
		catch (Exception exception) when (exception is OperatorException or OperationCanceledException)
		{
			// A host already gone answers nothing; the header then says the session ended rather than guessing why.
		}

		return stream.ExitedUtc;
	}

	/// <summary>Merges what a read brought, keeping a reader at the bottom where they were, and only then.</summary>
	private void Took(Action absorb)
	{
		var wasAtBottom = AtBottom();
		absorb();
		ShowState();

		// Scrolling a reader who had gone back to look at something is the surest way to make a live stream unusable.
		if (wasAtBottom) StickToBottom();
	}

	private void Failed(Exception exception) => _report?.Invoke(exception);

	private void ShowState()
	{
		var stream = _stream;

		DroppedBar.IsOpen = stream?.HasNotice == true;
		DroppedBar.Message = stream?.Notice ?? string.Empty;

		var empty = stream is null || stream.IsEmpty;
		Empty.Text = stream?.EmptyText ?? string.Empty;
		Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
		Scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
	}

	private bool AtBottom() => Scroll.ScrollableHeight <= 0 || Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24;

	/// <summary>
	/// How many times a scroll to the bottom is retried. The repeater realises rows as they come into view, so each pass
	/// discovers more extent than the last and one pass lands short.
	/// </summary>
	private const int StickPasses = 3;

	private void StickToBottom(int pass = 0)
	{
		// After the current layout pass, because rows just added have no height until the repeater has arranged them.
		DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
		{
			StreamRows.UpdateLayout();
			Scroll.UpdateLayout();
			Scroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);

			if (pass + 1 < StickPasses) StickToBottom(pass + 1);
		});
	}

	private void OnFilterChosen(object sender, SelectionChangedEventArgs args)
	{
		if (_stream is not { } stream) return;

		stream.Filter = FilterOf(FilterChoice.SelectedItem);
		ShowState();
		StickToBottom();
	}

	private static StreamFilter FilterOf(object? item) =>
		item is ComboBoxItem { Tag: string tag } && Enum.TryParse<StreamFilter>(tag, out var filter) ? filter : StreamFilter.All;

	private void OnRowTapped(object sender, TappedRoutedEventArgs args)
	{
		if (sender is FrameworkElement { Tag: StreamRow row }) _regard?.Invoke(row.Subject);
	}

	private void OnAskRow(object sender, RoutedEventArgs args)
	{
		if (sender is FrameworkElement { Tag: StreamRow { Subject: { } subject } }) _ask?.Invoke(subject, this);
	}

	/// <summary>The colour a row's "who" is drawn in: the app's own traffic quiet, a stop and a failure loud, an agent blue, you in the rose.</summary>
	public static Brush BrushFor(StreamTone tone) => Resource<Brush>(tone switch
	{
		StreamTone.Stop => "SystemFillColorCautionBrush",
		StreamTone.Critical => "SystemFillColorCriticalBrush",
		StreamTone.Agent => "AccentTextFillColorPrimaryBrush",
		StreamTone.You => "RoseAccentBrush",
		_ => "TextFillColorSecondaryBrush",
	}) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

	/// <summary>The face a row's text is set in: the mono one for a call or a log line, which are code rather than prose.</summary>
	public static FontFamily FontFor(bool mono) =>
		(mono ? Resource<FontFamily>("MonoFontFamily") : null) ?? FontFamily.XamlAutoFontFamily;

	/// <summary>A resource of the app's, looked up the way a ThemeResource would be at the moment it is asked for.</summary>
	private static T? Resource<T>(string key)
		where T : class =>
		Application.Current.Resources.TryGetValue(key, out var found) ? found as T : null;
}
