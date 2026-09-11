using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Panes;

/// <summary>
/// A held target: where it is stopped, the stack that got it there, and the buttons that move it.
/// <para>
/// The source of the selected frame is beside the stack with the line execution is on lit, because
/// that is what makes a step worth taking. A step whose only visible effect is an instruction offset
/// changing is one nobody can follow.
/// </para>
/// </summary>
public sealed partial class StackPane : UserControl
{
	private OperatorClient? _client;
	private Action<Exception>? _report;
	private InspectedSession? _session;
	private HoldKeeper? _holds;

	private StopInspection? _stop;

	/// <summary>The stop the frames on screen were read at, so a newer one is noticed.</summary>
	private long _readAt = -1;

	/// <summary>Where the session is now, which a click on Release needs and the poll knows.</summary>
	private long _stopSequence;

	/// <summary>
	/// The stop a reader handed back to its own timer, so the keeper is not asked for it again.
	/// Per stop rather than a mode, because letting this stop go is not a decision about the next
	/// one -- a reader who steps is asking to look at where the step landed.
	/// </summary>
	private long _releasedAt = -1;

	private bool _visible;

	private bool _stopped;

	private bool _reading;

	public StackPane() => InitializeComponent();

	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
	}

	public void Bind(InspectedSession session, HoldKeeper holds)
	{
		_session = session;
		_holds = holds;
		_stop = null;
		_readAt = -1;
		_releasedAt = -1;
		_stopped = false;
		StaleBar.Message = InspectorText.StopEnded;
		Show(null);
	}

	/// <summary>
	/// Becoming visible claims the hold and being hidden gives it up, both at once rather than on the
	/// next poll. A pane that waited for its poll to claim would let the leaving pane's release go out
	/// first, and the target is loose in between -- which is the whole failure the shared keeper
	/// exists to prevent.
	/// </summary>
	public void Showing(bool visible)
	{
		_visible = visible;
		_ = _holds?.Want(this, Wanted);
	}

	/// <summary>
	/// Whether this pane needs the target kept where it is: only while somebody can see it, only while
	/// there is a stop to hold, and not at a stop its reader has already handed back.
	/// </summary>
	private bool Wanted => _visible && _stopped && _stopSequence != _releasedAt;

	/// <summary>
	/// Takes each poll of the session. Frames are re-read when the stop's sequence moves, and not
	/// otherwise: a stopped target cannot move, so re-reading on the poll would only throw away the
	/// frame a reader had selected.
	/// </summary>
	public async void Observe(SessionRow row)
	{
		_stopSequence = row.StopSequence;
		_stopped = row.IsStopped;

		var settled = _holds?.Want(this, Wanted) ?? Task.CompletedTask;

		Steps(row.IsStopped);

		// The keeper's word beats the session's when it has one: the session says what the target is
		// doing, and the keeper says why this window could not make it wait.
		HoldText.Text = _holds?.Detail is { Length: > 0 } why ? why : row.ResumeLabel;
		ReleaseButton.Visibility = row.IsHeld ? Visibility.Visible : Visibility.Collapsed;

		// Frames outlive the stop they came from, deliberately: the target continuing is ordinary,
		// and a pane that emptied itself would take a reader's place with it and say nothing.
		StaleBar.IsOpen = !row.IsStopped && _stop is not null;

		if (!_visible || !row.IsStopped || row.StopSequence == _readAt || _reading) return;

		// After the hold, not beside it. A stack read under the safety timer can be answered and
		// stale before the first frame is on screen.
		await settled;
		await ReadAsync(row.StopSequence);
	}

	/// <summary>Reads the stack at a stop, which the keeper is already holding still.</summary>
	private async Task ReadAsync(long stopSequence)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			_reading = true;
			_readAt = stopSequence;

			var frames = await client.FramesAsync(session.SessionId, null, 0, null, CancellationToken.None);
			if (frames.Execution == LiveExecutionState.Running) return;

			var stop = new StopInspection(frames);
			_stop = stop;
			Show(stop);

			await ShowFrameAsync(stop);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_reading = false;
		}
	}

	/// <summary>
	/// Reads what the selected frame is made of: its source, and its arguments and locals. Both are
	/// per frame, so both are re-read when the selection moves and neither on the poll.
	/// </summary>
	private async Task ShowFrameAsync(StopInspection stop)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (stop.Selected is not { } frame) return;

		try
		{
			// The values first. They are what a step is taken to look at, and the source beside them
			// is already recognisable from the frame row's own file and line.
			var variables = await client.FrameVariablesAsync(
				session.SessionId, frame.Index, stop.ThreadId, CancellationToken.None);

			stop.Show(frame, variables);
			ShowVariables(stop);

			if (frame.Location is not { } location)
			{
				stop.ShowNothing("This frame is in a method metadata could not name, so there is no source to find.");
				ShowSource(stop);
				return;
			}

			var source = await client.MethodSourceAsync(session.SessionId, location, CancellationToken.None);
			stop.Show(frame, source);
			ShowSource(stop);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	private void Show(StopInspection? stop)
	{
		FrameRows.ItemsSource = stop?.Frames;
		FrameRows.SelectedItem = stop?.Selected;
		FramesCaption.Text = stop is null
			? "Call stack"
			: $"Call stack ({Format.Count(stop.Frames.Count, "frame")})";

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
		VariablesCaption.Text = stop is null || stop.Variables.Count == 0
			? "Values"
			: $"Values ({Format.Count(stop.Variables.Count, "value")})";

		var detail = stop?.VariablesDetail ?? string.Empty;
		VariablesDetail.Text = detail;
		VariablesDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// Fetches what is inside a value the reader has just opened.
	/// <para>
	/// The tree asks once per node, because <c>HasUnrealizedChildren</c> goes false as soon as the
	/// answer lands. Nothing here runs debuggee code: the host reads fields and elements out of
	/// memory, so opening a value cannot change what the stop was taken to look at.
	/// </para>
	/// </summary>
	private async void OnValueExpanding(TreeView sender, TreeViewExpandingEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (_stop is not { } stop) return;
		if (args.Item is not VariableNode node || !node.HasUnrealizedChildren || node.IsLoading) return;

		node.Loading();

		try
		{
			var expansion = await client.ValueAsync(
				session.SessionId,
				node.Path,
				stop.Selected?.Index ?? 0,
				stop.ThreadId,
				CancellationToken.None);

			if (expansion.Execution == LiveExecutionState.Running)
			{
				node.Failed(expansion.Detail ?? "The target is no longer stopped, so this value is gone.");
				return;
			}

			node.Fill(expansion);
		}
		catch (Exception exception)
		{
			node.Failed(exception.Message);
			_report?.Invoke(exception);
		}
	}

	/// <summary>
	/// Scrolls to the line execution is on.
	/// <para>
	/// Posted rather than done here, because the rows have only just been given to the repeater and
	/// nothing is realised until it has laid out. Best effort: a row the repeater has not built yet
	/// cannot be scrolled to, and the alternative is a reader looking at the top of a method while
	/// the target sits forty lines down.
	/// </para>
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

	/// <summary>Whether the target can be moved from here, which it can only be while it is stopped.</summary>
	private void Steps(bool stopped)
	{
		ContinueButton.IsEnabled = stopped;
		StepInButton.IsEnabled = stopped;
		StepOverButton.IsEnabled = stopped;
		StepOutButton.IsEnabled = stopped;
	}

	private async void OnFrameChosen(object sender, SelectionChangedEventArgs args)
	{
		if (_stop is not { } stop) return;
		if (FrameRows.SelectedItem is not FrameRow frame) return;
		if (ReferenceEquals(frame, stop.Selected)) return;

		stop.Selected = frame;
		await ShowFrameAsync(stop);
	}

	private async void OnContinue(object sender, RoutedEventArgs args) => await ResumeAsync(null);

	private async void OnStepIn(object sender, RoutedEventArgs args) => await ResumeAsync("in");

	private async void OnStepOver(object sender, RoutedEventArgs args) => await ResumeAsync("over");

	private async void OnStepOut(object sender, RoutedEventArgs args) => await ResumeAsync("out");

	/// <summary>
	/// Moves the target: a continue, or a step of one of the three kinds.
	/// <para>
	/// A step lands in a new stop with a sequence of its own, so nothing is re-read here. The next
	/// poll sees the sequence move and reads the stack the step arrived at, which is the same path a
	/// breakpoint hit takes.
	/// </para>
	/// </summary>
	private async Task ResumeAsync(string? mode)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			Steps(false);

			// The target is about to move, so it is no longer a stop this pane wants kept. Saying so
			// before the release is what stops the next poll taking the hold straight back and the
			// step then being answered by releasing it loudly.
			_stopped = false;

			if (_holds is { } holds) await holds.Want(this, Wanted);

			var moved = mode is null
				? await client.ContinueAsync(session.SessionId, CancellationToken.None)
				: await client.StepAsync(session.SessionId, mode, CancellationToken.None);

			if (moved.Detail is { Length: > 0 } detail)
			{
				_report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));
			}
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	/// <summary>
	/// Hands this stop back to its own safety timer, without moving the target.
	/// <para>
	/// Remembered against the stop rather than acted on once, or the next poll would want the hold
	/// again and take it straight back. The next stop is a new decision, so stepping re-arms it.
	/// </para>
	/// </summary>
	private async void OnRelease(object sender, RoutedEventArgs args)
	{
		if (_holds is not { } holds) return;

		_releasedAt = _stopSequence;
		await holds.Want(this, Wanted);
	}
}
