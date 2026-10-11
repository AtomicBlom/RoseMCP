using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.App.Inspector.Panes;

/// <summary>
/// The code view of a stopped app: every thread and any one's stack on the left, and the chosen frame's source, its
/// locals and arguments, and what the person watches in the middle.
/// <para>
/// Everything is read when the stop moves and when the person chooses another thread or frame, never on a timer: a
/// stopped target cannot move, so a poll would spend a request a second on an answer that cannot have changed. Each
/// stop's reads go with one cancellation, ended when the stop does, when the session does, and when the window
/// closes; an answer that lands after that is for a stop nobody is looking at, and is dropped.
/// </para>
/// </summary>
public sealed partial class CodePane : UserControl
{
	private OperatorClient? _client;
	private Action<Exception>? _report;
	private Action<NoteSubject, FrameworkElement>? _ask;
	private Action<NoteSubject?>? _regard;
	private Action<string>? _located;

	private InspectedSession? _session;
	private CodeInspection? _inspection;

	/// <summary>Cancels every read made for the stop on screen, when the stop, the session or the window goes.</summary>
	private CancellationTokenSource? _reads;

	private long _stopSequence = -1;

	public CodePane()
	{
		InitializeComponent();
		WatchRows.ItemsSource = Watches.Rows;
	}

	/// <summary>What the person watches, for the life of the window: the same app attached again keeps its watches.</summary>
	public WatchList Watches { get; } = new();

	/// <param name="client">The app's client.</param>
	/// <param name="report">Where a failed read is said.</param>
	/// <param name="ask">Opens "Ask an agent" about a row, beside an element.</param>
	/// <param name="regard">Says which row was last selected, which the window's shortcut asks about.</param>
	/// <param name="located">Takes where the stop is once the stopped thread's stack is read, for the header.</param>
	public void Attach(
		OperatorClient client,
		Action<Exception> report,
		Action<NoteSubject, FrameworkElement> ask,
		Action<NoteSubject?> regard,
		Action<string> located)
	{
		_client = client;
		_report = report;
		_ask = ask;
		_regard = regard;
		_located = located;
	}

	/// <summary>Points the view at a session, forgetting whatever stop the last one was at.</summary>
	public void Bind(InspectedSession session)
	{
		Stop();

		_session = session;
		_inspection = null;
		_stopSequence = -1;
		StaleBar.IsOpen = false;
		Show(null);
	}

	/// <summary>
	/// Ends every read in flight and marks what is on screen as over, for a session that has ended or a window that is
	/// closing. The rows stay, under a banner, so the last place the app stopped can still be read.
	/// </summary>
	public void Stop(string? why = null)
	{
		CancelReads();
		Watches.Stale();
		_inspection?.End();

		if (why is not null && _inspection is not null)
		{
			StaleBar.Message = why;
			StaleBar.IsOpen = true;
		}
	}

	/// <summary>
	/// Takes each poll of the session. A new stop is read whole -- its threads, the stopped thread's stack, the top
	/// frame's values and source, and every watch -- and the last stop's rows are marked over the moment the target
	/// moves, though they stay on screen.
	/// </summary>
	public async void Observe(SessionRow row)
	{
		if (_session is not { } session) return;

		var stopped = row.IsStopped && !row.IsEnded;
		var sequence = stopped ? row.StopSequence : 0;

		StaleBar.Message = InspectorText.StopEnded;
		StaleBar.IsOpen = !stopped && _inspection is not null;

		if (sequence == _stopSequence) return;

		_stopSequence = sequence;
		CancelReads();
		Watches.Stale();
		_inspection?.End();

		if (!stopped) return;

		var inspection = new CodeInspection(session.SessionId, sequence, row.StopThreadId);
		_inspection = inspection;
		_reads = new CancellationTokenSource();

		await ReadStopAsync(inspection, _reads.Token);
	}

	/// <summary>
	/// Cancels the stop's reads. Not disposed: a handler still awaiting one of them reads its token when it resumes, and
	/// a source with no timer holds nothing a collection does not take back.
	/// </summary>
	private void CancelReads()
	{
		_reads?.Cancel();
		_reads = null;
	}

	/// <summary>Reads a stop: its threads and the stopped thread's stack side by side, then the top frame's contents.</summary>
	private async Task ReadStopAsync(CodeInspection inspection, CancellationToken cancellationToken)
	{
		if (_client is not { } client) return;

		try
		{
			var threads = client.ThreadsAsync(inspection.SessionId, cancellationToken);
			var frames = client.FramesAsync(inspection.SessionId, null, 0, null, cancellationToken);

			var stack = inspection.Take(await frames);
			if (!IsCurrent(inspection)) return;

			Show(inspection);
			if (stack is not null) _located?.Invoke(inspection.Location);

			inspection.Take(await threads);
			if (!IsCurrent(inspection)) return;

			ShowThreads(inspection);

			if (stack is not null) await ShowFrameAsync(inspection, stack, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The stop moved on, the session ended or the window closed; nothing is waiting for this answer.
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	private bool IsCurrent(CodeInspection inspection) => ReferenceEquals(inspection, _inspection) && !inspection.IsOver;

	/// <summary>
	/// Reads what the selected frame is made of: its values, its source, and every watch at it. All three are per frame,
	/// so all are read again when the selection moves and none on the poll.
	/// </summary>
	private async Task ShowFrameAsync(CodeInspection inspection, StopInspection stop, CancellationToken cancellationToken)
	{
		if (_client is not { } client) return;
		if (stop.Selected is not { } frame) return;

		try
		{
			// The values first. They are what a step is taken to look at, and the source beside them is already
			// recognisable from the frame row's own file and line.
			var variables = await client.FrameVariablesAsync(inspection.SessionId, frame.Index, stop.ThreadId, cancellationToken);
			if (!IsCurrent(inspection)) return;

			stop.Show(frame, variables);
			ShowVariables(stop);

			var watching = Watches.ReadAsync(
				new WatchContext(inspection.SessionId, inspection.StopSequence, stop.ThreadId, frame),
				(path, at, token) => client.ValueAsync(at.SessionId, path, at.Frame?.Index ?? 0, at.ThreadId, token),
				cancellationToken);

			if (frame.Location is not { } location)
			{
				stop.ShowNothing("This frame is in a method metadata could not name, so there is no source to find.");
				ShowSource(stop);
			}
			else
			{
				var source = await client.MethodSourceAsync(inspection.SessionId, location, cancellationToken);
				if (!IsCurrent(inspection)) return;

				stop.Show(frame, source);
				ShowSource(stop);
			}

			await watching;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// See ReadStopAsync.
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	private void Show(CodeInspection? inspection)
	{
		ShowThreads(inspection);
		ShowStack(inspection);
	}

	private void ShowThreads(CodeInspection? inspection)
	{
		var threads = inspection?.Threads ?? [];

		ThreadRows.ItemsSource = threads;
		ThreadRows.SelectedItem = inspection?.SelectedThread;
		ThreadsCaption.Text = threads.Count == 0 ? "Threads" : $"Threads ({Format.Count(threads.Count, "thread")})";

		var detail = inspection is null ? InspectorText.NotStopped : inspection.ThreadsDetail;
		ThreadsDetail.Text = detail;
		ThreadsDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ShowStack(CodeInspection? inspection)
	{
		var stop = inspection?.Stack;

		StackCaption.Text = inspection?.StackCaption ?? "Stack";
		FrameRows.ItemsSource = stop?.Frames;
		FrameRows.SelectedItem = stop?.Selected;

		ShowSource(stop);
		ShowVariables(stop);
	}

	private void ShowSource(StopInspection? stop)
	{
		SourceCaption.Text = stop?.Selected?.Method ?? "Source";
		SourceRows.ItemsSource = stop?.Source;

		var detail = stop?.SourceDetail ?? string.Empty;
		SourceDetail.Text = detail;
		SourceDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		BringCurrentIntoView(stop);
	}

	private void ShowVariables(StopInspection? stop)
	{
		VariableRows.ItemsSource = stop?.Variables;
		VariablesCaption.Text = stop?.Selected is { } frame ? $"Locals · frame {frame.Index}" : "Locals";

		var detail = stop?.VariablesDetail ?? string.Empty;
		VariablesDetail.Text = detail;
		VariablesDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// Scrolls to the line execution is on, after the repeater has laid the rows out -- nothing is realised before
	/// then. Best effort: a row the repeater has not built yet cannot be scrolled to.
	/// </summary>
	private void BringCurrentIntoView(StopInspection? stop)
	{
		if (stop is null) return;

		var index = -1;
		for (var at = 0; at < stop.Source.Count; at++)
		{
			if (!stop.Source[at].IsCurrent) continue;

			index = at;
			break;
		}

		if (index < 0) return;

		DispatcherQueue.TryEnqueue(() =>
		{
			SourceRows.UpdateLayout();
			SourceRows.TryGetElement(index)?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.4 });
		});
	}

	/// <summary>Shows another thread's stack, read at the stop on screen.</summary>
	private async void OnThreadChosen(object sender, SelectionChangedEventArgs args)
	{
		if (_client is not { } client || _inspection is not { } inspection || _reads is not { } reads) return;
		if (ThreadRows.SelectedItem is not ThreadRow thread) return;

		_regard?.Invoke(Subject(thread));

		if (inspection.IsOver || !inspection.Choose(thread)) return;

		ShowStack(inspection);

		try
		{
			var frames = await client.FramesAsync(inspection.SessionId, thread.Id, 0, null, reads.Token);
			if (!IsCurrent(inspection) || inspection.Take(frames) is not { } stack) return;

			ShowStack(inspection);
			await ShowFrameAsync(inspection, stack, reads.Token);
		}
		catch (OperationCanceledException) when (reads.IsCancellationRequested)
		{
			// See ReadStopAsync.
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	private async void OnFrameChosen(object sender, SelectionChangedEventArgs args)
	{
		if (_inspection is not { Stack: { } stop } inspection) return;
		if (FrameRows.SelectedItem is not FrameRow frame) return;

		_regard?.Invoke(Subject(frame));

		if (ReferenceEquals(frame, stop.Selected) || inspection.IsOver || _reads is not { } reads) return;

		stop.Selected = frame;
		await ShowFrameAsync(inspection, stop, reads.Token);
	}

	/// <summary>
	/// Fetches what is inside a value the reader has just opened. Nothing here runs debuggee code: the host reads
	/// fields and elements out of memory, so opening a value cannot change what the stop was taken to look at.
	/// </summary>
	private async void OnValueExpanding(TreeView sender, TreeViewExpandingEventArgs args)
	{
		if (_client is not { } client || _inspection is not { Stack: { } stop } inspection || _reads is not { } reads) return;
		if (args.Item is not VariableNode node || !node.HasUnrealizedChildren || node.IsLoading) return;

		node.Loading();

		try
		{
			var expansion = await client.ValueAsync(inspection.SessionId, node.Path, stop.Selected?.Index ?? 0, stop.ThreadId, reads.Token);

			if (expansion.Execution == LiveExecutionState.Running)
			{
				node.Failed(expansion.Detail ?? "The target is no longer stopped, so this value is gone.");
				return;
			}

			node.Fill(expansion);
		}
		catch (OperationCanceledException) when (reads.IsCancellationRequested)
		{
			node.Failed("The target moved on, so this value is gone.");
		}
		catch (Exception exception)
		{
			node.Failed(exception.Message);
			_report?.Invoke(exception);
		}
	}

	/// <summary>
	/// Notes the value a reader clicked, which the window's shortcut asks about. The tree hands over the node wrapping
	/// the item rather than the item in ItemsSource mode, so both are looked through.
	/// </summary>
	private void OnValueInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
	{
		var node = args.InvokedItem switch
		{
			VariableNode value => value,
			TreeViewNode { Content: VariableNode value } => value,
			_ => null,
		};

		if (node is not null) _regard?.Invoke(Subject(node));
	}

	private void OnWatchChosen(object sender, SelectionChangedEventArgs args)
	{
		if (WatchRows.SelectedItem is WatchRow watch) _regard?.Invoke(watch.Subject);
	}

	/// <summary>Adds a watch on Enter, and reads it at once where the target is stopped.</summary>
	private void OnWatchKey(object sender, KeyRoutedEventArgs args)
	{
		if (args.Key != Windows.System.VirtualKey.Enter) return;

		args.Handled = true;
		AddWatch(WatchInput.Text);
	}

	private void OnWatchValue(object sender, RoutedEventArgs args)
	{
		if (sender is FrameworkElement { Tag: VariableNode node }) AddWatch(node.Path);
	}

	private void AddWatch(string text)
	{
		var row = Watches.Add(text, out var problem);

		WatchProblem.Text = problem ?? string.Empty;
		WatchProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
		if (row is null) return;

		WatchInput.Text = string.Empty;
		ReadWatches();
	}

	private void OnRemoveWatch(object sender, RoutedEventArgs args)
	{
		if (sender is FrameworkElement { Tag: WatchRow row }) Watches.Remove(row);
	}

	/// <summary>Reads every watch again at the frame on screen, for a watch just added.</summary>
	private async void ReadWatches()
	{
		if (_client is not { } client || _inspection is not { Stack: { Selected: { } frame } stop } inspection) return;
		if (inspection.IsOver || _reads is not { } reads) return;

		try
		{
			await Watches.ReadAsync(
				new WatchContext(inspection.SessionId, inspection.StopSequence, stop.ThreadId, frame),
				(path, at, token) => client.ValueAsync(at.SessionId, path, at.Frame?.Index ?? 0, at.ThreadId, token),
				reads.Token);
		}
		catch (OperationCanceledException) when (reads.IsCancellationRequested)
		{
			// See ReadStopAsync.
		}
	}

	/// <summary>"Ask an agent" from a row's own menu: whatever the row is, about the stop on screen.</summary>
	private void OnAskRow(object sender, RoutedEventArgs args)
	{
		var subject = (sender as FrameworkElement)?.Tag switch
		{
			ThreadRow thread => Subject(thread),
			FrameRow frame => Subject(frame),
			VariableNode node => Subject(node),
			WatchRow watch => watch.Subject,
			_ => null,
		};

		if (subject is not null) _ask?.Invoke(subject, this);
		else _report?.Invoke(new OperatorException(OperatorFailure.Refused, AskOver));
	}

	/// <summary>Why a row cannot be asked about: the stop it was read at is over, so what it names is gone.</summary>
	private const string AskOver =
		"That row is from a stop that is over, so what it names is gone. Ask about it at the next stop, or about the event in the stream.";

	private NoteSubject? Subject(ThreadRow thread) =>
		_inspection is { IsOver: false } inspection ? AskAgent.Thread(inspection.SessionId, thread) : null;

	private NoteSubject? Subject(FrameRow frame) =>
		_inspection is { IsOver: false, Stack: { } stop } inspection ? AskAgent.Frame(inspection.SessionId, stop.ThreadId, frame) : null;

	private NoteSubject? Subject(VariableNode node) =>
		_inspection is { IsOver: false, Stack: { } stop } inspection
			? AskAgent.Value(inspection.SessionId, stop.ThreadId, stop.Selected, node)
			: null;
}
