using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// The loop every pane of a RoseMCP window refreshes on.
/// <para>
/// Two of its properties are the reason it is not a timer, and both are tested here: the body is
/// awaited, so a slow answer costs a skipped interval rather than a growing queue of requests; and a
/// body that throws is reported and the loop carries on, because a poll that failed once is a reason
/// to say so and not a reason to stop watching. A third is that it yields every iteration, which is
/// what keeps a window's message pump alive.
/// </para>
/// <para>
/// Every wait below is a spin on a condition with a generous ceiling rather than a sleep of a fixed
/// length, and every one is bounded. A test that sleeps long enough on this machine is a test that
/// fails on a loaded build agent, and a test that fails by hanging is worse than the bug it looks
/// for -- which this file learned the hard way, since the yield above was missing and the suite
/// spun a core until it was killed.
/// </para>
/// </summary>
public sealed class PollLoopTests
{
	/// <summary>Long enough to be certain on a loaded machine, short enough not to wedge a suite.</summary>
	private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Short enough that a test wanting several iterations gets them at once, and positive, which
	/// matters: a zero interval is the case that used to wedge the caller.
	/// </summary>
	private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(1);

	private static async Task Until(Func<bool> condition, string what)
	{
		var deadline = DateTime.UtcNow + Ceiling;

		while (DateTime.UtcNow < deadline)
		{
			if (condition()) return;

			await Task.Delay(10);
		}

		Assert.Fail($"waited {Ceiling.TotalSeconds:0}s and {what}");
	}

	[Test]
	public async Task Runs_the_body_at_once_rather_than_after_the_first_interval()
	{
		var runs = 0;

		// An interval far longer than the ceiling, so anything observed came from the immediate run.
		var loop = new PollLoop(
			_ =>
			{
				Interlocked.Increment(ref runs);
				return Task.CompletedTask;
			},
			TimeSpan.FromMinutes(5),
			_ => { });

		loop.Start();

		await Until(() => Volatile.Read(ref runs) > 0, "the body never ran");
		loop.Stop();
	}

	/// <summary>
	/// The regression that cost a suite run. With no interval and a body that finishes synchronously,
	/// nothing in the loop yielded: it ran as a plain infinite loop on the caller's stack and
	/// <see cref="PollLoop.Start"/> never returned. On a window that is the message pump stopping,
	/// which looks like the app having died rather than like a bug in a poll.
	/// <para>
	/// Bounded by racing the call against a delay, so a regression fails this test rather than
	/// hanging the suite.
	/// </para>
	/// </summary>
	[Test]
	public async Task Returns_from_start_with_no_interval_and_a_synchronous_body()
	{
		var loop = new PollLoop(_ => Task.CompletedTask, TimeSpan.Zero, _ => { });

		var starting = Task.Run(loop.Start);
		var first = await Task.WhenAny(starting, Task.Delay(Ceiling));

		loop.Stop();

		Assert.Same(starting, first);
	}

	/// <summary>
	/// A body still running must not have another started under it. A timer would stack requests
	/// against a host that has slowed down; awaiting means exactly one is ever in flight.
	/// </summary>
	[Test]
	public async Task Never_runs_the_body_twice_at_once()
	{
		var inFlight = 0;
		var overlapped = false;
		var runs = 0;

		var loop = new PollLoop(
			async _ =>
			{
				if (Interlocked.Increment(ref inFlight) > 1) overlapped = true;

				await Task.Delay(20);

				Interlocked.Decrement(ref inFlight);
				Interlocked.Increment(ref runs);
			},
			Brisk,
			_ => { });

		loop.Start();

		await Until(() => Volatile.Read(ref runs) >= 3, "the body ran fewer than three times");
		loop.Stop();

		Assert.False(overlapped, "the loop awaits the body, so two runs never overlap");
	}

	[Test]
	public async Task Reports_a_failure_and_keeps_going()
	{
		var runs = 0;
		var failures = 0;

		var loop = new PollLoop(
			_ =>
			{
				Interlocked.Increment(ref runs);
				throw new InvalidOperationException("the host did not answer");
			},
			Brisk,
			_ => Interlocked.Increment(ref failures));

		loop.Start();

		await Until(() => Volatile.Read(ref runs) >= 3, "a throwing body stopped the loop");
		loop.Stop();

		Assert.True(Volatile.Read(ref failures) >= 3, "every failure was reported");
	}

	/// <summary>
	/// A kick is for the case where something has just changed and the next scheduled poll is too far
	/// away to be how a reader finds out.
	/// </summary>
	[Test]
	public async Task Runs_the_body_again_when_kicked()
	{
		var runs = 0;
		var loop = new PollLoop(
			_ =>
			{
				Interlocked.Increment(ref runs);
				return Task.CompletedTask;
			},
			TimeSpan.FromMinutes(5),
			_ => { });

		loop.Start();
		await Until(() => Volatile.Read(ref runs) == 1, "the body never ran");

		// Kicked until it takes. A kick lands on the wait the loop is in, and immediately after the
		// first run it may not have reached that wait yet -- so one kick can legitimately find
		// nothing to complete.
		await Until(
			() =>
			{
				loop.Kick();
				return Volatile.Read(ref runs) >= 2;
			},
			"a kick did not run the body again");

		loop.Stop();
	}

	[Test]
	public async Task Stops_waiting_when_stopped()
	{
		var runs = 0;
		var loop = new PollLoop(
			_ =>
			{
				Interlocked.Increment(ref runs);
				return Task.CompletedTask;
			},
			Brisk,
			_ => { });

		loop.Start();
		await Until(() => Volatile.Read(ref runs) > 0, "the body never ran");

		loop.Stop();
		await Until(() => !loop.IsRunning, "the loop was still running after Stop");

		var settled = Volatile.Read(ref runs);
		await Task.Delay(50);

		Assert.Equal(settled, Volatile.Read(ref runs));
	}

	[Test]
	public async Task Starting_a_running_loop_does_nothing()
	{
		var loop = new PollLoop(_ => Task.CompletedTask, TimeSpan.FromMinutes(5), _ => { });

		loop.Start();
		await Until(() => loop.IsRunning, "the loop never started");

		loop.Start();

		Assert.True(loop.IsRunning);
		loop.Stop();
	}
}
