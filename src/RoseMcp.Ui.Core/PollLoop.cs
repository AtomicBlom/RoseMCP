namespace RoseMcp.Ui.Core;

/// <summary>
/// Runs an async body on an interval until stopped, awaiting each run before waiting for the next.
/// <para>
/// A timer is the obvious alternative and it is wrong for this: a tick that fires while the previous
/// request is still in flight starts a second one, and against a host that has slowed down the queue
/// only grows. Awaiting the body means a slow answer costs a skipped interval rather than a backlog.
/// </para>
/// <para>
/// It resumes on the context it was started from, which for a window is the UI thread. That is the
/// point: the body writes to bound rows, and doing that from a pool thread is a cross-thread failure
/// that appears as a corrupted list rather than an exception.
/// </para>
/// </summary>
/// <param name="body">What to run each time. Exceptions go to <paramref name="onError"/>.</param>
/// <param name="interval">How long to wait between runs, measured from the end of one to the start of the next.</param>
/// <param name="onError">
/// Told about anything the body threw. The loop carries on afterwards: a poll that failed once is a
/// reason to say so, not a reason to stop watching.
/// </param>
public sealed class PollLoop(Func<CancellationToken, Task> body, TimeSpan interval, Action<Exception> onError)
{
	private CancellationTokenSource? _stopping;
	private Task? _running;

	// What a Kick completes: the source the current iteration's wait is racing against a delay.
	// Armed just before that wait and cleared after it, so a Kick arriving while the body runs still
	// lands on the wait about to happen rather than being dropped.
	private TaskCompletionSource? _kick;

	/// <summary>Whether the loop is running. False before the first <see cref="Start"/> and after a <see cref="Stop"/>.</summary>
	public bool IsRunning => _running is { IsCompleted: false };

	/// <summary>
	/// Starts the loop, running the body immediately rather than after the first interval: a pane
	/// that has just been shown should not be blank for a second first. Starting a running loop does
	/// nothing.
	/// </summary>
	public void Start()
	{
		if (IsRunning) return;

		_stopping?.Dispose();
		_stopping = new CancellationTokenSource();
		_running = RunAsync(_stopping.Token);
	}

	/// <summary>
	/// Stops the loop and cancels whatever the body is waiting on. It does not wait for the body to
	/// finish: the caller is usually a UI thread the body needs in order to finish at all.
	/// </summary>
	public void Stop() => _stopping?.Cancel();

	/// <summary>
	/// Runs the body now instead of waiting out the interval, if the loop is running. For the case
	/// where something has just changed and the next scheduled poll is too far away to be the way a
	/// reader finds out.
	/// </summary>
	public void Kick() => _kick?.TrySetResult();

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			// First, and unconditionally. Task.Delay(TimeSpan.Zero) returns an already-completed
			// task, so with a body that also finishes synchronously nothing below ever yields: the
			// loop runs as a plain infinite loop on the caller's stack, Start never returns, and for
			// a window that means the message pump stops and the app is dead on screen. A yield
			// costs nothing and makes the loop cooperative whatever its interval is.
			await Task.Yield();

			try
			{
				await body(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				onError(exception);
			}

			// Armed before the wait and cleared after it, so a Kick that arrives while the body is
			// running is not lost: it completes the source this iteration is about to wait on.
			_kick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			try
			{
				await Task.WhenAny(Task.Delay(interval, cancellationToken), _kick.Task);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			finally
			{
				_kick = null;
			}
		}
	}
}
