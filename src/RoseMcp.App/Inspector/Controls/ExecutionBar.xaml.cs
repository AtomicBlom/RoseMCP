using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.App.Inspector.Controls;

/// <summary>
/// The header's stop line: why the app is stopped and where, who stopped it and when it lets go, Hold beside that
/// countdown -- and what moves the target, in the standard debugger glyphs with their usual shortcuts.
/// <para>
/// In the header rather than inside a view, because moving a target is a thing you do to the session rather than to
/// a stack: wanting to continue while reading the stream or the visual tree is the ordinary case.
/// </para>
/// <para>
/// The target is held only when a person asks: Hold, or Pause, since somebody who pressed Pause is by definition
/// present. Being on screen is not asking -- the code view is always on screen, so a hold taken for it would keep
/// every agent's stop for as long as the window stayed open, which is what a breakpoint's safety timer exists to
/// prevent.
/// </para>
/// </summary>
public sealed partial class ExecutionBar : UserControl
{
	private OperatorClient? _client;
	private Action<Exception>? _report;
	private InspectedSession? _session;
	private HoldKeeper? _holds;

	private bool _stopped;
	private bool _working;
	private bool _ended;
	private long _stopSequence;

	/// <summary>
	/// The stop this bar asked for, so the target stays where somebody put it. Per stop, because resuming is that
	/// decision being withdrawn.
	/// </summary>
	private long _pausedAt = -1;

	/// <summary>The stop a person pressed Hold at, which is held until they continue or release it.</summary>
	private long _heldAt = -1;

	public ExecutionBar() => InitializeComponent();

	/// <summary>Raised when Detach is pressed; the window owns the session and ends it.</summary>
	public event EventHandler? DetachRequested;

	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
	}

	public void Bind(InspectedSession session, HoldKeeper holds)
	{
		_session = session;
		_holds = holds;
		_pausedAt = -1;
		_heldAt = -1;
		_stopped = false;
		_ended = false;
		StopLocation.Text = string.Empty;
	}

	/// <summary>Takes each poll of the session: what can be pressed, and what the stop is on.</summary>
	public void Observe(SessionRow row)
	{
		if (row.StopSequence != _stopSequence) StopLocation.Text = string.Empty;

		_stopSequence = row.StopSequence;
		_stopped = row.IsStopped;
		_ended = row.IsEnded;

		_ = _holds?.Want(this, Wanted);

		Show(row);
	}

	/// <summary>Recomputes the countdown between polls, which is what moves on its own.</summary>
	public void Tick(SessionRow row) => Show(row);

	/// <summary>Where the stop is, once the stopped thread's stack has been read.</summary>
	public void Locate(string location) => StopLocation.Text = _stopped ? location : string.Empty;

	/// <summary>
	/// The window is done with this session: nothing to move, and nothing wanted held. A session that has ended is not
	/// asked to give its hold back -- its target is gone, and the request would only come back as a failure to report --
	/// while one the window is merely leaving is, where <paramref name="release"/> says so.
	/// </summary>
	public void End(bool release = true)
	{
		_ended = true;
		_stopped = false;
		_pausedAt = -1;
		_heldAt = -1;
		if (release) _ = _holds?.Want(this, false);
		else _holds = null;
		Buttons();
	}

	private void Show(SessionRow row)
	{
		StopTitle.Text = row.StopTitle;

		// The keeper's word beats the session's when it has one: the session says what the target is doing, and the
		// keeper says why this window could not make it wait.
		StopFacts.Text = _holds?.Detail is { Length: > 0 } why ? $"{row.StopFacts}{Format.Separator}{why}" : row.StopFacts;

		HoldButton.Visibility = row.IsStopped && !row.IsEnded ? Visibility.Visible : Visibility.Collapsed;
		HoldButton.Content = row.IsHeld ? "Release" : "Hold";
		ToolTipService.SetToolTip(HoldButton, row.IsHeld
			? "Give the stop back to its own safety timer, without moving the target"
			: "Keep it stopped until you continue");

		Buttons();
	}

	/// <summary>Whether this bar is holding the target still: only for a stop somebody asked for here, and only until they move it on.</summary>
	private bool Wanted => _stopped && !_ended && (_stopSequence == _pausedAt || _stopSequence == _heldAt);

	/// <summary>
	/// Pause is for a running target and the rest are for a stopped one, so exactly one half is ever live. Everything
	/// goes dead while a request is in flight, because a second press would be a step issued into a target that is
	/// already being moved. A glyph is dimmed with its button, since a drawn one does not follow the button's state.
	/// </summary>
	private void Buttons()
	{
		var live = _session is not null && !_ended;

		Enable(PauseButton, live && !_stopped && !_working);

		var canMove = live && _stopped && !_working;
		Enable(ContinueButton, canMove);
		Enable(StepInButton, canMove);
		Enable(StepOverButton, canMove);
		Enable(StepOutButton, canMove);
		Enable(DetachButton, live && !_working);
	}

	private static void Enable(Button button, bool enabled)
	{
		button.IsEnabled = enabled;
		if (button.Content is UIElement glyph) glyph.Opacity = enabled ? 1 : 0.35;
	}

	private void OnDetach(object sender, RoutedEventArgs args) => DetachRequested?.Invoke(this, EventArgs.Empty);

	/// <summary>Holds this stop until the person continues, or gives it back to its own timer if it is held.</summary>
	private async void OnHold(object sender, RoutedEventArgs args)
	{
		if (_holds is not { } holds) return;

		var holding = _stopSequence == _heldAt || _stopSequence == _pausedAt;
		if (holding || holds.Held)
		{
			_heldAt = -1;
			_pausedAt = -1;
		}
		else
		{
			_heldAt = _stopSequence;
		}

		await holds.Want(this, Wanted);
	}

	private async void OnPause(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session || !PauseButton.IsEnabled) return;

		try
		{
			_working = true;
			Buttons();

			var paused = await client.BreakAsync(session.SessionId, CancellationToken.None);

			if (paused.Stop is { } stop)
			{
				// Held from here on, whether this call is what stopped it or a breakpoint got there first: either way
				// somebody is standing in front of it waiting to read something.
				_pausedAt = stop.EventSequence;
				_stopped = true;
				_stopSequence = stop.EventSequence;

				if (_holds is { } holds)
				{
					// The stop first, then the want. The keeper drops what it believes it holds when the stop moves,
					// so taking a hold for a stop it has not heard of yet is a hold taken and then immediately taken
					// again when the poll catches up.
					_ = holds.AtStop(stop.EventSequence);
					await holds.Want(this, Wanted);
				}
			}

			if (!paused.Paused && paused.Detail is { Length: > 0 } detail)
			{
				_report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));
			}
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_working = false;
			Buttons();
		}
	}

	private async void OnContinue(object sender, RoutedEventArgs args) => await ResumeAsync(null);

	private async void OnStepIn(object sender, RoutedEventArgs args) => await ResumeAsync("in");

	private async void OnStepOver(object sender, RoutedEventArgs args) => await ResumeAsync("over");

	private async void OnStepOut(object sender, RoutedEventArgs args) => await ResumeAsync("out");

	/// <summary>
	/// Moves the target: a continue, or a step of one of the three kinds.
	/// <para>
	/// A step lands in a new stop with a sequence of its own, so nothing is re-read here. The next poll sees the
	/// sequence move and every view reads what is there, which is the same path a breakpoint hit takes. A shortcut
	/// pressed while its button cannot be is ignored, since an accelerator reaches the handler either way.
	/// </para>
	/// </summary>
	private async Task ResumeAsync(string? mode)
	{
		if (_client is not { } client || _session is not { } session || !ContinueButton.IsEnabled) return;

		try
		{
			_working = true;
			Buttons();

			// The target is about to move, so this bar no longer wants the stop kept -- said before the release, so
			// the next poll cannot take the hold straight back and leave the step to be answered by releasing it
			// loudly.
			_pausedAt = -1;
			_heldAt = -1;
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
		finally
		{
			_working = false;
			Buttons();
		}
	}
}
