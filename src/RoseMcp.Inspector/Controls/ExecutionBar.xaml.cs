using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Controls;

/// <summary>
/// Where the target is made to move: pause, continue, the three steps, and what is keeping the stop
/// it is at.
/// <para>
/// Above the tabs rather than inside the stack pane, because moving a target is not a thing you do
/// to a stack -- it is a thing you do to the session, and wanting it while reading the event tail or
/// the visual tree is the ordinary case. Buried in one pane it was only reachable from the tab that
/// is least useful while the target is running.
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
	private long _stopSequence;

	/// <summary>
	/// The stop this bar asked for, so the target stays where somebody put it.
	/// <para>
	/// A person who presses Pause is by definition present, so the stop is worth holding past its
	/// safety timer -- otherwise pausing from the events tab is a target that starts again on its
	/// own half a minute later, which reads as the button not having worked. Per stop, because
	/// resuming is that decision being withdrawn.
	/// </para>
	/// </summary>
	private long _pausedAt = -1;

	public ExecutionBar() => InitializeComponent();

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
		_stopped = false;
	}

	/// <summary>Takes each poll of the session: what can be pressed, and what the stop is on.</summary>
	public void Observe(SessionRow row)
	{
		_stopSequence = row.StopSequence;
		_stopped = row.IsStopped;

		_ = _holds?.Want(this, Wanted);

		Buttons();

		// The keeper's word beats the session's when it has one: the session says what the target is
		// doing, and the keeper says why this window could not make it wait.
		HoldText.Text = _holds?.Detail is { Length: > 0 } why ? why : row.ResumeLabel;
		ReleaseButton.Visibility = row.IsHeld ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// Whether this bar is holding the target still: only for a stop somebody asked for here, and
	/// only until they move it on.
	/// </summary>
	private bool Wanted => _stopped && _stopSequence == _pausedAt;

	/// <summary>
	/// Pause is for a running target and the rest are for a stopped one, so exactly one half is ever
	/// live. Everything goes dead while a request is in flight, because a second press would be a
	/// step issued into a target that is already being moved.
	/// </summary>
	private void Buttons()
	{
		PauseButton.IsEnabled = !_stopped && !_working;

		var canMove = _stopped && !_working;
		ContinueButton.IsEnabled = canMove;
		StepInButton.IsEnabled = canMove;
		StepOverButton.IsEnabled = canMove;
		StepOutButton.IsEnabled = canMove;
	}

	private async void OnPause(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			_working = true;
			Buttons();

			var paused = await client.BreakAsync(session.SessionId, CancellationToken.None);

			if (paused.Stop is { } stop)
			{
				// Held from here on, whether this call is what stopped it or a breakpoint got there
				// first: either way somebody is standing in front of it waiting to read something.
				_pausedAt = stop.EventSequence;
				_stopped = true;
				_stopSequence = stop.EventSequence;

				if (_holds is { } holds)
				{
					// The stop first, then the want. The keeper drops what it believes it holds when
					// the stop moves, so taking a hold for a stop it has not heard of yet is a hold
					// taken and then immediately taken again when the poll catches up.
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
	/// A step lands in a new stop with a sequence of its own, so nothing is re-read here. The next
	/// poll sees the sequence move and every pane reads what is there, which is the same path a
	/// breakpoint hit takes.
	/// </para>
	/// </summary>
	private async Task ResumeAsync(string? mode)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			_working = true;
			Buttons();

			// The target is about to move, so this bar no longer wants the stop kept -- said before
			// the release, so the next poll cannot take the hold straight back and leave the step to
			// be answered by releasing it loudly.
			_pausedAt = -1;
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

	/// <summary>
	/// Hands this stop back to its own safety timer without moving the target, and stops this bar
	/// asking for it again on the next poll.
	/// </summary>
	private async void OnRelease(object sender, RoutedEventArgs args)
	{
		if (_holds is not { } holds) return;

		_pausedAt = -1;
		await holds.Want(this, Wanted);
	}
}
