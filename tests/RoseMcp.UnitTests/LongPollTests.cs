using Microsoft.Extensions.Hosting;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// A view held open until it holds something else: answered at once where nothing is waited for, as soon as the
/// view changes where its owner pulses, a moment later where it does not, with what it holds when the wait runs out
/// or the host stops, and not at all when the reader has gone.
/// </summary>
public sealed class LongPollTests
{
	/// <summary>Far longer than any of these should take, so a wait that is not ended early fails the test rather than passing late.</summary>
	private const int LongWait = 30;

	private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

	[Test]
	public async Task A_read_with_no_version_or_no_wait_is_answered_at_once(CancellationToken cancellationToken)
	{
		var view = new View("v1");
		var poll = new LongPoll();

		(await poll.WaitAsync(view.Read, Version, after: null, LongWait, changed: null, cancellationToken)).ShouldBe("v1");
		(await poll.WaitAsync(view.Read, Version, after: "v1", waitSeconds: 0, changed: null, cancellationToken)).ShouldBe("v1");
		view.Reads.ShouldBe(2, "each answered by one read, and none waited");
	}

	[Test]
	public async Task A_view_that_already_differs_is_answered_at_once(CancellationToken cancellationToken)
	{
		var view = new View("v2");

		(await new LongPoll().WaitAsync(view.Read, Version, after: "v1", LongWait, changed: null, cancellationToken)).ShouldBe("v2");
	}

	/// <summary>The owner's pulse ends the wait as soon as the view has changed, rather than at the next recheck.</summary>
	[Test]
	public async Task A_pulse_answers_the_wait_with_the_changed_view(CancellationToken cancellationToken)
	{
		var view = new View("v1");
		var changed = new ChangeSignal();

		var waiting = new LongPoll().WaitAsync(view.Read, Version, after: "v1", LongWait, changed, cancellationToken);
		await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		waiting.IsCompleted.ShouldBeFalse("nothing has changed");

		view.Current = "v2";
		changed.Pulse();

		(await waiting.WaitAsync(Soon, cancellationToken)).ShouldBe("v2");
	}

	/// <summary>A change nobody pulsed for is still seen, a recheck later: a row expiring, another broker's file growing.</summary>
	[Test]
	public async Task A_change_nobody_pulsed_for_is_seen_at_the_next_recheck(CancellationToken cancellationToken)
	{
		var view = new View("v1");

		var waiting = new LongPoll().WaitAsync(view.Read, Version, after: "v1", LongWait, changed: new ChangeSignal(), cancellationToken);
		view.Current = "v2";

		(await waiting.WaitAsync(LongPoll.Recheck + Soon, cancellationToken)).ShouldBe("v2");
	}

	/// <summary>A pulse that changed nothing this view shows goes back to waiting.</summary>
	[Test]
	public async Task A_pulse_that_changed_nothing_keeps_the_wait(CancellationToken cancellationToken)
	{
		var view = new View("v1");
		var changed = new ChangeSignal();

		var waiting = new LongPoll().WaitAsync(view.Read, Version, after: "v1", LongWait, changed, cancellationToken);
		changed.Pulse();
		await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

		waiting.IsCompleted.ShouldBeFalse();

		view.Current = "v3";
		changed.Pulse();
		(await waiting.WaitAsync(Soon, cancellationToken)).ShouldBe("v3");
	}

	[Test]
	public async Task A_wait_that_runs_out_answers_the_view_as_it_is(CancellationToken cancellationToken)
	{
		var view = new View("v1");

		var answer = await new LongPoll()
			.WaitAsync(view.Read, Version, after: "v1", waitSeconds: 1, changed: null, cancellationToken)
			.WaitAsync(Soon, cancellationToken);

		answer.ShouldBe("v1");
	}

	/// <summary>A host stopping answers every open wait at once, so a window's poll never holds up a stop.</summary>
	[Test]
	public async Task The_host_stopping_answers_the_wait(CancellationToken cancellationToken)
	{
		var view = new View("v1");
		using var lifetime = new Lifetime();
		var poll = new LongPoll(lifetime);

		var waiting = poll.WaitAsync(view.Read, Version, after: "v1", LongWait, new ChangeSignal(), cancellationToken);
		lifetime.StopApplication();

		(await waiting.WaitAsync(Soon, cancellationToken)).ShouldBe("v1");

		var afterStop = poll.WaitAsync(view.Read, Version, after: "v1", LongWait, changed: null, cancellationToken);
		(await afterStop.WaitAsync(Soon, cancellationToken)).ShouldBe("v1", "a wait begun once the host is stopping is answered at once");
	}

	/// <summary>A reader that goes away is not answered: there is nobody to answer, and the request's filter makes it empty.</summary>
	[Test]
	public async Task A_reader_that_goes_away_ends_the_wait(CancellationToken cancellationToken)
	{
		var view = new View("v1");
		using var reader = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		var waiting = new LongPoll().WaitAsync(view.Read, Version, after: "v1", LongWait, new ChangeSignal(), reader.Token);
		await reader.CancelAsync();

		await Should.ThrowAsync<OperationCanceledException>(async () => await waiting.WaitAsync(Soon, cancellationToken));
	}

	[Test]
	public async Task A_pulse_completes_what_was_waiting_and_not_what_waits_next()
	{
		var changed = new ChangeSignal();
		var first = changed.Next;

		changed.Pulse();

		await first;
		changed.Next.IsCompleted.ShouldBeFalse();
	}

	private static string? Version(string view) => view;

	/// <summary>A view that is its own version, counting its reads.</summary>
	private sealed class View(string initial)
	{
		private volatile string _current = initial;
		private int _reads;

		public string Current
		{
			get => _current;
			set => _current = value;
		}

		public int Reads => Volatile.Read(ref _reads);

		public string Read()
		{
			Interlocked.Increment(ref _reads);
			return _current;
		}
	}

	private sealed class Lifetime : IHostApplicationLifetime, IDisposable
	{
		private readonly CancellationTokenSource _stopping = new();

		public CancellationToken ApplicationStarted => CancellationToken.None;

		public CancellationToken ApplicationStopping => _stopping.Token;

		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication() => _stopping.Cancel();

		public void Dispose() => _stopping.Dispose();
	}
}
