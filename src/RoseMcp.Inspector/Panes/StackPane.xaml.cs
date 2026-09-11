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
	/// <summary>
	/// How long a hold is taken for. Long enough that reading a stack and opening a few values never
	/// runs out, short enough that a reader who walks away does not leave somebody's application
	/// frozen for the host's whole ten-minute cap.
	/// </summary>
	private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(120);

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private InspectedSession? _session;

	private StopInspection? _stop;

	/// <summary>The stop the frames on screen were read at, so a newer one is noticed.</summary>
	private long _readAt = -1;

	private bool _visible;
	private bool _reading;

	public StackPane() => InitializeComponent();

	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
	}

	public void Bind(InspectedSession session)
	{
		_session = session;
		_stop = null;
		_readAt = -1;
		Show(null);
	}

	/// <summary>
	/// A pane nobody can see holds nothing. The hold exists so a reader can work, so it is released
	/// the moment they look somewhere else -- otherwise a tab left in the background keeps somebody's
	/// application stopped.
	/// </summary>
	public void Showing(bool visible)
	{
		_visible = visible;

		if (!visible) Release();
	}

	/// <summary>
	/// Takes each poll of the session. Frames are re-read when the stop's sequence moves, and not
	/// otherwise: a stopped target cannot move, so re-reading on the poll would only throw away the
	/// frame a reader had selected.
	/// </summary>
	public async void Observe(SessionRow row)
	{
		Steps(row.IsStopped);
		HoldText.Text = row.ResumeLabel;
		ReleaseButton.Visibility = row.IsHeld ? Visibility.Visible : Visibility.Collapsed;

		// Frames outlive the stop they came from, deliberately: the target continuing is ordinary,
		// and a pane that emptied itself would take a reader's place with it and say nothing.
		StaleBar.IsOpen = !row.IsStopped && _stop is not null;

		if (!_visible || !row.IsStopped || row.StopSequence == _readAt || _reading) return;

		await ReadAsync(row.StopSequence);
	}

	/// <summary>Reads the stack at a stop, and takes a hold so it stays still while it is read.</summary>
	private async Task ReadAsync(long stopSequence)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			_reading = true;
			_readAt = stopSequence;

			// The hold first. A stack read under the safety timer can be answered and stale before
			// the first frame is on screen.
			await client.HoldAsync(session.SessionId, (int)HoldFor.TotalSeconds, release: false, CancellationToken.None);

			var frames = await client.FramesAsync(session.SessionId, null, 0, null, CancellationToken.None);
			if (frames.Execution == LiveExecutionState.Running) return;

			var stop = new StopInspection(frames);
			_stop = stop;
			Show(stop);

			await ShowSourceAsync(stop);
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

	/// <summary>Reads the selected frame's source, or says why there is none to read.</summary>
	private async Task ShowSourceAsync(StopInspection stop)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (stop.Selected is not { } frame) return;

		if (frame.Location is not { } location)
		{
			stop.ShowNothing("This frame has no method metadata could name, so there is no source to find.");
			ShowSource(stop);
			return;
		}

		try
		{
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
		await ShowSourceAsync(stop);
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

			// The hold has to go first, or the step is issued into a target somebody is holding
			// still and the stop it lands in is held by a hold taken for the stop before.
			await client.HoldAsync(session.SessionId, null, release: true, CancellationToken.None);

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

	private async void OnRelease(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			await client.HoldAsync(session.SessionId, null, release: true, CancellationToken.None);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	/// <summary>
	/// Gives a hold back without waiting for the answer or reporting a failure.
	/// <para>
	/// Called from the pane being hidden and from the window closing, neither of which has anywhere
	/// to put an error and neither of which can wait. A hold that outlives this is bounded by the
	/// host's own cap, so the worst case is a target that frees itself a couple of minutes later.
	/// </para>
	/// </summary>
	private void Release()
	{
		if (_client is not { } client || _session is not { } session) return;

		_ = client.HoldAsync(session.SessionId, null, release: true, CancellationToken.None)
			.ContinueWith(static held => _ = held.Exception, TaskScheduler.Default);
	}
}
