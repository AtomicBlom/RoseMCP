using Microsoft.Extensions.Hosting;

namespace RoseMcp.Broker;

/// <summary>
/// Holds an operator read open until the view it reads holds something other than what the reader already has,
/// which is how a window keeps a view current without asking every second: the reader passes back the version
/// its last answer carried, and is answered when that version would differ, or when its wait runs out.
/// <para>
/// A view is compared by its version, worked out from what the view holds, rather than by a counter its owner
/// bumps. Each view is several objects' state -- a workspace row is the worker's, its activity log's and the
/// health record's -- and a counter would have to be bumped at every place any of them changes, where missing one
/// is a window that silently stops updating. Reading the view again cannot miss anything. So a wait reads the
/// view again every <see cref="Recheck"/>, and sooner where the view's owner pulses a <see cref="ChangeSignal"/>
/// -- the activity record and the client table do, on every call -- which is what makes a call show up at once.
/// </para>
/// <para>
/// Nothing is held on a singleton between reads: a wait is one request's linked token, a timer and a reference
/// to the signal's current task, all gone when the request ends. It ends early, answering what the view holds, when
/// the host starts stopping, so a window's open poll never holds up a deploy for the rest of its wait; and it is
/// cancelled outright when the reader goes away, since then there is nobody to answer.
/// </para>
/// </summary>
public sealed class LongPoll
{
	/// <summary>The longest a read waits, however long it asks for: the bound the live-app host's event wait has.</summary>
	public const int MaxWaitSeconds = 60;

	/// <summary>How often a waiting read reads its view again, which bounds how late it sees a change nobody pulsed for.</summary>
	public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(1);

	private readonly CancellationToken _stopping;

	/// <summary>Ends every wait early when <paramref name="lifetime"/>'s host starts stopping, where there is one.</summary>
	public LongPoll(IHostApplicationLifetime? lifetime = null) =>
		_stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;

	/// <summary>
	/// The view <paramref name="read"/> gives, once its version differs from <paramref name="after"/> or the wait is
	/// over: at once where <paramref name="after"/> is empty or <paramref name="waitSeconds"/> is not positive, and
	/// otherwise after up to <paramref name="waitSeconds"/>, bounded by <see cref="MaxWaitSeconds"/>.
	/// </summary>
	/// <param name="read">Reads the view. Called again on every pulse and every <see cref="Recheck"/>, so it reads state already in memory and starts no work.</param>
	/// <param name="versionOf">The version a view carries.</param>
	/// <param name="after">The version the reader already has.</param>
	/// <param name="waitSeconds">How long the reader will wait for a change.</param>
	/// <param name="changed">Pulsed by the view's owner when it changes, where it has one.</param>
	/// <param name="aborted">The reader going away.</param>
	/// <exception cref="OperationCanceledException">The reader went away.</exception>
	public async Task<T> WaitAsync<T>(
		Func<T> read,
		Func<T, string?> versionOf,
		string? after,
		int waitSeconds,
		ChangeSignal? changed,
		CancellationToken aborted)
	{
		var pulse = changed?.Next;
		var view = read();

		if (string.IsNullOrEmpty(after) || waitSeconds <= 0) return view;

		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(aborted, _stopping);
		waiting.CancelAfter(TimeSpan.FromSeconds(Math.Min(waitSeconds, MaxWaitSeconds)));

		while (string.Equals(versionOf(view), after, StringComparison.Ordinal))
		{
			using (var tick = CancellationTokenSource.CreateLinkedTokenSource(waiting.Token))
			{
				var recheck = Task.Delay(Recheck, tick.Token);
				await Task.WhenAny(recheck, pulse ?? recheck);

				// Ends the timer of a wait a pulse ended, rather than leaving it to run out.
				await tick.CancelAsync();
			}

			aborted.ThrowIfCancellationRequested();

			pulse = changed?.Next;
			view = read();

			if (waiting.IsCancellationRequested) break;
		}

		return view;
	}
}
