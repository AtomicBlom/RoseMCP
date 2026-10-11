using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The refresh a worker runs after every call: one at a time, and never ending with the last request
/// unanswered. The broker learns of a rebuilt analyzer only through it, so a request lost in a burst is a
/// reload that never happens.
/// </summary>
public sealed class CoalescedRefreshTests
{
	/// <summary>
	/// Many callers change what a refresh would read and ask for one, all at once and repeatedly. However
	/// the requests interleave with the runs, no two runs overlap, and once the burst is over a run has read
	/// the last state -- which a run begun before the last change could not have.
	/// </summary>
	[Test]
	[Repeat(20)]
	public async Task A_burst_runs_one_refresh_at_a_time_and_the_last_one_sees_the_last_state(CancellationToken cancellationToken)
	{
		var state = 0;
		var observed = -1;
		var inFlight = 0;
		var mostInFlight = 0;

		var refresh = new CoalescedRefresh(
			async () =>
			{
				var now = Interlocked.Increment(ref inFlight);
				InterlockedMax(ref mostInFlight, now);

				await Task.Yield();
				Volatile.Write(ref observed, Volatile.Read(ref state));

				Interlocked.Decrement(ref inFlight);
			},
			() => true);

		var callers = Enumerable.Range(0, 8).Select(caller => Task.Run(() =>
		{
			for (var call = 0; call < 200; call++)
			{
				Interlocked.Increment(ref state);
				refresh.Request();
			}
		}, cancellationToken));

		await Task.WhenAll(callers);
		var last = Volatile.Read(ref state);

		await WaitUntilAsync(() => !refresh.Running && Volatile.Read(ref observed) == last, cancellationToken);

		mostInFlight.ShouldBe(1);
		Volatile.Read(ref observed).ShouldBe(last);
	}

	/// <summary>A refresh that throws does not swallow the request after it.</summary>
	[Test]
	public async Task A_refresh_that_fails_still_lets_the_next_request_run(CancellationToken cancellationToken)
	{
		var runs = 0;

		var refresh = new CoalescedRefresh(
			() => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("The worker went away.") : Task.CompletedTask,
			() => true);

		refresh.Request();
		await WaitUntilAsync(() => !refresh.Running && Volatile.Read(ref runs) == 1, cancellationToken);

		refresh.Request();
		await WaitUntilAsync(() => !refresh.Running && Volatile.Read(ref runs) == 2, cancellationToken);
	}

	[Test]
	public void A_stopped_owner_is_not_refreshed()
	{
		var runs = 0;
		var refresh = new CoalescedRefresh(() => Task.FromResult(Interlocked.Increment(ref runs)), () => false);

		refresh.Request();

		refresh.Running.ShouldBeFalse();
		runs.ShouldBe(0);
	}

	private static void InterlockedMax(ref int target, int value)
	{
		var current = Volatile.Read(ref target);
		while (value > current)
		{
			var seen = Interlocked.CompareExchange(ref target, value, current);
			if (seen == current) return;

			current = seen;
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

		while (!condition())
		{
			if (DateTime.UtcNow > deadline) throw new TimeoutException("The refresh did not settle within 10 s.");

			await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
		}
	}
}
