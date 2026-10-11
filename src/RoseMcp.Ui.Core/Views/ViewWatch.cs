namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// Keeps one operator view current: reads it at once, then holds a long poll open with the version it last
/// had until the broker answers with something else, for as long as it runs.
/// <para>
/// A read that fails is reported and tried again after <see cref="RetryAfter"/>, never at once. The loop has no
/// interval of its own -- the broker's wait is the interval -- so a broker that is not there answers every read
/// immediately with a refused connection, and retrying without a pause would spin a core against a closed port
/// for as long as the window stays open. The version is forgotten on a failure, so the first read after the
/// broker returns waits for nothing: a restarted broker is said to be serving the moment it is, not a wait later.
/// </para>
/// <para>
/// It runs on the context it was started from, which for a window is the UI thread, so <c>onView</c> can merge
/// into bound rows directly. <see cref="Stop"/> cancels the read in flight; a view nobody can see must not hold a
/// request open on the broker on the reader's behalf.
/// </para>
/// </summary>
/// <typeparam name="T">The view, as the route answers it.</typeparam>
public sealed class ViewWatch<T>
	where T : class
{
	/// <summary>
	/// How long a read asks the broker to hold. The broker bounds it at a minute; half that means a broker that
	/// stopped answering mid-wait is noticed while somebody is still looking.
	/// </summary>
	public const int WaitSeconds = 30;

	/// <summary>How long a read that failed waits before it is tried again, unless <see cref="Kick"/> says to try now.</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(3);

	private readonly Func<string?, int, CancellationToken, Task<T>> _read;
	private readonly Func<T, string?> _versionOf;
	private readonly Action<T> _onView;
	private readonly Action<Exception> _onFailure;
	private readonly TimeSpan _retryAfter;
	private readonly PollLoop _loop;

	// Bumped by Kick, so an answer to a question asked before it is dropped rather than shown as the new one's.
	private int _asked;

	// Guards Version and the read in flight, which Kick reaches from the window while a read is being made.
	private readonly Lock _gate = new();

	// The read in flight, which Kick cancels; null between reads.
	private CancellationTokenSource? _reading;

	// Completed by Kick, to end a retry's pause early; a fresh one is armed for each pause.
	private TaskCompletionSource _retryNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <param name="read">
	/// Reads the view: given the version the reader has and how long to wait for another, or no version and no
	/// wait for a read answered at once.
	/// </param>
	/// <param name="versionOf">The version a view carries.</param>
	/// <param name="onView">Given each view read, on the context the watch was started from.</param>
	/// <param name="onFailure">Given each read that failed, before the pause; the watch carries on after it.</param>
	/// <param name="retryAfter">How long a failed read pauses; <see cref="RetryAfter"/> where null.</param>
	public ViewWatch(
		Func<string?, int, CancellationToken, Task<T>> read,
		Func<T, string?> versionOf,
		Action<T> onView,
		Action<Exception> onFailure,
		TimeSpan? retryAfter = null)
	{
		_read = read;
		_versionOf = versionOf;
		_onView = onView;
		_onFailure = onFailure;
		_retryAfter = retryAfter ?? RetryAfter;
		_loop = new PollLoop(ReadAsync, TimeSpan.Zero, onFailure);
	}

	/// <summary>The last view read, or null before the first.</summary>
	public T? Last { get; private set; }

	/// <summary>The version the next read waits past, or null when it is to be answered at once.</summary>
	public string? Version { get; private set; }

	/// <summary>Whether the watch is running.</summary>
	public bool IsRunning => _loop.IsRunning;

	/// <summary>Starts watching, reading at once. Starting a running watch does nothing.</summary>
	public void Start() => _loop.Start();

	/// <summary>Stops watching and cancels the read in flight.</summary>
	public void Stop() => _loop.Stop();

	/// <summary>
	/// Asks again at once rather than waiting past the version held: for a filter that changed, so the wait in flight
	/// is about the wrong question, or for a token that changed, so a pause after a refusal is over. The read in flight
	/// is cancelled, and an answer to the old question that arrives anyway is dropped rather than shown as an answer
	/// to the new one.
	/// </summary>
	public void Kick()
	{
		Interlocked.Increment(ref _asked);

		lock (_gate)
		{
			Version = null;
			_reading?.Cancel();
		}

		_retryNow.TrySetResult();
		_loop.Kick();
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		var asked = Volatile.Read(ref _asked);
		string? version;
		using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		lock (_gate)
		{
			version = Version;
			_reading = reading;
		}

		T view;

		try
		{
			view = await _read(version, version is null ? 0 : WaitSeconds, reading.Token);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException) when (reading.IsCancellationRequested)
		{
			// Kicked: the question changed under the read, and the loop asks the new one at once.
			return;
		}
		catch (Exception exception)
		{
			// A read kicked while it failed was about the old question; the new one is asked at once.
			if (asked != Volatile.Read(ref _asked)) return;

			lock (_gate) Version = null;

			// Armed before the failure is reported, so a Kick that reporting leads to -- a new token taken in
			// answer to a refusal -- ends this pause rather than one already over.
			var retryNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_retryNow = retryNow;
			_onFailure(exception);

			await Task.WhenAny(Task.Delay(_retryAfter, cancellationToken), retryNow.Task);
			cancellationToken.ThrowIfCancellationRequested();
			return;
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_reading, reading)) _reading = null;
			}
		}

		// An answer to a question asked before a Kick is an answer to the wrong question: shown, it would put the
		// old filter's page under the new filter. The loop asks the new question next.
		if (asked != Volatile.Read(ref _asked)) return;

		lock (_gate) Version = _versionOf(view);

		Last = view;
		_onView(view);
	}
}
