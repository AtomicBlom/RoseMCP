using System.Collections.Concurrent;

using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// What keeps a view of the RoseMCP window current: a read at once, then a long poll past the version it has; a
/// read that fails is retried after a pause rather than at once, and the read after it waits for nothing, so a
/// broker that comes back is seen to be back the moment it is.
/// </summary>
public sealed class ViewWatchTests
{
	private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

	/// <summary>The first read waits for nothing; each after it passes back the version it was given, with the wait.</summary>
	[Test]
	public void Reads_at_once_then_waits_past_the_version_it_has()
	{
		var asked = new BlockingCollection<(string? After, int Wait)>();
		var reads = 0;

		var watch = new ViewWatch<string>(
			async (after, wait, cancellationToken) =>
			{
				asked.Add((after, wait));
				if (Interlocked.Increment(ref reads) > 2) await Task.Delay(Timeout.Infinite, cancellationToken);

				return $"v{reads}";
			},
			view => view,
			_ => { },
			_ => { });

		watch.Start();

		Take(asked).ShouldBe((null, 0));
		Take(asked).ShouldBe(("v1", ViewWatch<string>.WaitSeconds));
		Take(asked).ShouldBe(("v2", ViewWatch<string>.WaitSeconds));

		watch.Stop();
	}

	/// <summary>
	/// A broker that is not there refuses every read at once; the watch says so, pauses, and asks again -- never in a
	/// tight loop -- and the read after the failure waits for nothing.
	/// </summary>
	[Test]
	public void A_failed_read_is_reported_and_retried_after_a_pause()
	{
		var asked = new BlockingCollection<(string? After, int Wait, DateTime At)>();
		var failures = new BlockingCollection<Exception>();
		var reads = 0;
		var pause = TimeSpan.FromMilliseconds(300);

		var watch = new ViewWatch<string>(
			async (after, wait, cancellationToken) =>
			{
				asked.Add((after, wait, DateTime.UtcNow));

				switch (Interlocked.Increment(ref reads))
				{
					case 1:
						return "v1";
					case 2:
						throw new OperatorException(OperatorFailure.Unreachable, "gone", null);
					default:
						await Task.Delay(Timeout.Infinite, cancellationToken);
						return "never";
				}
			},
			view => view,
			_ => { },
			failures.Add,
			pause);

		watch.Start();

		Take(asked);
		var failed = Take(asked);
		failed.After.ShouldBe("v1");

		Take(failures).ShouldBeOfType<OperatorException>();

		var retried = Take(asked);
		(retried.At - failed.At).ShouldBeGreaterThanOrEqualTo(pause - TimeSpan.FromMilliseconds(50), "a refused read is not retried at once");
		retried.After.ShouldBeNull("the version is forgotten, so the read after a failure waits for nothing");

		watch.Stop();
	}

	/// <summary>A kick ends a retry's pause at once, which is what a new token from the tray's launch is for.</summary>
	[Test]
	public void A_kick_ends_the_pause_after_a_failure()
	{
		var asked = new BlockingCollection<DateTime>();
		var failures = new BlockingCollection<Exception>();
		var reads = 0;

		var watch = new ViewWatch<string>(
			async (_, _, cancellationToken) =>
			{
				asked.Add(DateTime.UtcNow);
				if (Interlocked.Increment(ref reads) == 1) throw new OperatorException(OperatorFailure.Unauthorized, "old token", null);

				await Task.Delay(Timeout.Infinite, cancellationToken);
				return "never";
			},
			view => view,
			_ => { },
			failures.Add,
			retryAfter: TimeSpan.FromMinutes(10));

		watch.Start();
		var first = Take(asked);
		Take(failures);

		watch.Kick();

		(Take(asked) - first).ShouldBeLessThan(TimeSpan.FromMinutes(1));
		watch.Stop();
	}

	private static T Take<T>(BlockingCollection<T> collection)
	{
		collection.TryTake(out var item, Soon).ShouldBeTrue("the watch did not ask in time");
		return item!;
	}
}
