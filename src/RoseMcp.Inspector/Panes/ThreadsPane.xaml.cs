using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Panes;

/// <summary>
/// Every managed thread of a stopped target, and what each is sitting in.
/// <para>
/// It is the other half of the stack pane's question. A stack says where one thread is; a thread
/// list says how many others there are and what they are doing, which is what tells a deadlock from
/// a slow method -- three threads in <c>WaitSleepJoin</c> under one that holds a lock is a shape a
/// single stack cannot show.
/// </para>
/// </summary>
public sealed partial class ThreadsPane : UserControl
{
	private OperatorClient? _client;
	private Action<Exception>? _report;
	private InspectedSession? _session;
	private HoldKeeper? _holds;

	/// <summary>The stop the threads on screen were read at, so a newer one is noticed.</summary>
	private long _readAt = -1;

	private bool _visible;
	private bool _stopped;
	private bool _reading;

	public ThreadsPane() => InitializeComponent();

	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
	}

	public void Bind(InspectedSession session, HoldKeeper holds)
	{
		_session = session;
		_holds = holds;
		_readAt = -1;
		_stopped = false;
		Show(null, string.Empty);
	}

	/// <summary>
	/// Becoming visible claims the hold and being hidden gives it up, both at once rather than on
	/// the next poll. A pane that waited for its poll to claim would let the leaving pane's release
	/// go out first, and the target is loose in between -- which is the whole failure the shared
	/// keeper exists to prevent.
	/// </summary>
	public void Showing(bool visible)
	{
		_visible = visible;
		_ = _holds?.Want(this, Wanted);
	}

	/// <summary>
	/// Whether this pane needs the target kept where it is: only while somebody can see it, and only
	/// while there is a stop to hold.
	/// </summary>
	private bool Wanted => _visible && _stopped;

	/// <summary>
	/// Takes each poll of the session. Threads are re-read when the stop's sequence moves, and not
	/// otherwise: a stopped target's threads cannot go anywhere, so a poll that re-read them would
	/// spend a request a second on an answer that cannot have changed.
	/// </summary>
	public async void Observe(SessionRow row)
	{
		_stopped = row.IsStopped;

		var settled = _holds?.Want(this, Wanted) ?? Task.CompletedTask;

		HoldText.Text = _holds?.Detail is { Length: > 0 } why ? why : row.ResumeLabel;

		if (!_visible) return;

		if (!row.IsStopped)
		{
			// The list stays up under a banner rather than being cleared, the way the stack pane's
			// frames do: the target continuing is ordinary, and an emptied pane says nothing at all.
			DetailBar.Message = InspectorText.NotStopped;
			DetailBar.IsOpen = true;
			return;
		}

		if (row.StopSequence == _readAt || _reading) return;

		await settled;
		await ReadAsync(row.StopSequence);
	}

	/// <summary>Reads the threads at a stop, with the hold already taken so the answer outlives itself.</summary>
	private async Task ReadAsync(long stopSequence)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			_reading = true;
			_readAt = stopSequence;

			var threads = await client.ThreadsAsync(session.SessionId, CancellationToken.None);

			// Not an error and not an empty list: the target moved between the poll and the read,
			// which is what the safety timer is for. The next stop reads again.
			if (threads.Execution == LiveExecutionState.Running)
			{
				DetailBar.Message = threads.Detail ?? InspectorText.NotStopped;
				DetailBar.IsOpen = true;
				return;
			}

			Show([.. threads.Threads.Select(thread => new ThreadRow(thread))], threads.Detail ?? string.Empty);
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

	private void Show(IReadOnlyList<ThreadRow>? threads, string detail)
	{
		ThreadRows.ItemsSource = threads;
		ThreadsCaption.Text = threads is null
			? "Threads"
			: $"Threads ({Format.Count(threads.Count, "thread")})";

		DetailBar.Message = detail;
		DetailBar.IsOpen = detail.Length > 0;
	}
}
