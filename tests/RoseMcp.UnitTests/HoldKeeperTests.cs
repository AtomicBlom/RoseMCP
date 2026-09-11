using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The one hold on a target, shared by the panes that read a stop.
/// <para>
/// What is asserted here is the bookkeeping, because that is where a shared hold goes wrong and the
/// failure is invisible: a target that resumes under a reader looks exactly like a target whose
/// safety timer expired, and both look like the debugger working.
/// </para>
/// <para>
/// Every test runs on a pump rather than on the thread pool, because the keeper is single-threaded
/// by design and batches whatever a caller says in one block. Letting continuations run on another
/// thread would test a keeper nothing uses, and race its own reader list while doing it.
/// </para>
/// </summary>
public sealed class HoldKeeperTests
{
	private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// One hold however many panes are reading. The host has one, so two panes taking their own is
	/// not two holds -- it is the same one, taken twice, and given back once too often.
	/// </summary>
	[Test]
	public void Two_readers_share_one_hold() => OnOneThread(pump =>
	{
		var host = new Host();
		var keeper = new HoldKeeper(host.Ask);
		var stack = new object();
		var threads = new object();

		pump.Run(keeper.Want(stack, true));
		pump.Run(keeper.Want(threads, true));

		Assert.Equal(1, host.Takes);
		Assert.True(keeper.Held);

		// The second reader leaving is not the last word: somebody is still reading.
		pump.Run(keeper.Want(threads, false));

		Assert.Equal(0, host.Releases);
		Assert.True(keeper.Held);

		pump.Run(keeper.Want(stack, false));

		Assert.Equal(1, host.Releases);
		Assert.False(keeper.Held);
	});

	/// <summary>
	/// Saying the same thing again asks the host nothing. Readers speak on every poll, so a keeper
	/// that acted on each one would issue a request a second at somebody's stopped application.
	/// </summary>
	[Test]
	public void Saying_it_again_asks_for_nothing() => OnOneThread(pump =>
	{
		var host = new Host();
		var keeper = new HoldKeeper(host.Ask);
		var reader = new object();

		pump.Run(keeper.Want(reader, true));
		pump.Run(keeper.Want(reader, true));
		pump.Run(keeper.Want(reader, true));

		Assert.Equal(1, host.Takes);
	});

	/// <summary>
	/// Changing tab hands the hold over rather than dropping it. Both panes speak in one pass, so
	/// reconciling on the first of them would release the hold and take it again -- and the target
	/// is free in between, which is long enough for its own timer to move it.
	/// </summary>
	[Test]
	public void A_reader_arriving_as_another_leaves_keeps_the_hold() => OnOneThread(pump =>
	{
		var host = new Host();
		var keeper = new HoldKeeper(host.Ask);
		var stack = new object();
		var threads = new object();

		pump.Run(keeper.Want(stack, true));
		Assert.Equal(1, host.Takes);

		// One pass, the way a tab change tells every pane before any of them is answered.
		var leaving = keeper.Want(stack, false);
		var arriving = keeper.Want(threads, true);
		pump.Run(Task.WhenAll(leaving, arriving));

		Assert.Equal(0, host.Releases);
		Assert.Equal(1, host.Takes);
		Assert.True(keeper.Held);
	});

	/// <summary>
	/// A new stop is a new hold. Continuing clears the hold in the host, so the sequence moving means
	/// this keeper holds nothing whatever it believed a moment ago.
	/// </summary>
	[Test]
	public void A_stop_that_moves_is_held_again() => OnOneThread(pump =>
	{
		var host = new Host();
		var keeper = new HoldKeeper(host.Ask);
		var reader = new object();

		pump.Run(keeper.AtStop(11));
		pump.Run(keeper.Want(reader, true));
		Assert.Equal(1, host.Takes);

		pump.Run(keeper.AtStop(12));

		Assert.Equal(2, host.Takes);
		Assert.Equal(0, host.Releases);
	});

	/// <summary>
	/// A target that is not stopped cannot be held, and is not asked twice about it. The next stop
	/// asks afresh, so the refusal is about this stop rather than about the session.
	/// </summary>
	[Test]
	public void A_refusal_belongs_to_the_stop_that_earned_it() => OnOneThread(pump =>
	{
		var host = new Host { Stopped = false };
		var keeper = new HoldKeeper(host.Ask);
		var reader = new object();

		pump.Run(keeper.AtStop(11));
		pump.Run(keeper.Want(reader, true));

		Assert.False(keeper.Held);
		Assert.Equal("Nothing is stopped, so there is no stop to hold.", keeper.Detail);

		// Asked again at the same stop, by a reader leaving and coming back.
		pump.Run(keeper.Want(reader, false));
		pump.Run(keeper.Want(reader, true));
		Assert.Equal(1, host.Takes);

		host.Stopped = true;
		pump.Run(keeper.AtStop(12));

		Assert.Equal(2, host.Takes);
		Assert.True(keeper.Held);
		Assert.Equal(string.Empty, keeper.Detail);
	});

	/// <summary>
	/// A failure counts as the refusal too. Retrying every second would be a window hammering a tray
	/// that cannot answer, and the message is kept so a pane can say why the target is loose.
	/// </summary>
	[Test]
	public void A_hold_that_fails_says_so_and_is_not_asked_again() => OnOneThread(pump =>
	{
		var host = new Host { Throws = new InvalidOperationException("the tray is not listening") };
		var reported = new List<Exception>();
		var keeper = new HoldKeeper(host.Ask, reported.Add);
		var reader = new object();

		pump.Run(keeper.Want(reader, true));
		pump.Run(keeper.Want(reader, false));
		pump.Run(keeper.Want(reader, true));

		Assert.Equal(1, host.Takes);
		Assert.False(keeper.Held);
		Assert.Equal("the tray is not listening", keeper.Detail);
		Assert.Single(reported);
	});

	/// <summary>
	/// Renewed close to the deadline and not before, so a reader who takes their time never sees the
	/// gap and one who does not never costs the target a second request.
	/// </summary>
	[Test]
	public void A_hold_is_renewed_as_it_runs_out() => OnOneThread(pump =>
	{
		var host = new Host { DeadlineUtc = Now.AddSeconds(120) };
		var keeper = new HoldKeeper(host.Ask);
		var reader = new object();

		pump.Run(keeper.Want(reader, true));
		Assert.Equal(1, host.Takes);

		pump.Run(keeper.Tick(Now.AddSeconds(30)));
		Assert.Equal(1, host.Takes);

		pump.Run(keeper.Tick(Now.AddSeconds(70)));
		Assert.Equal(2, host.Takes);
	});

	/// <summary>Nothing is renewed for nobody: a hold with no reader is one that is being given back.</summary>
	[Test]
	public void A_hold_nobody_wants_is_not_renewed() => OnOneThread(pump =>
	{
		var host = new Host { DeadlineUtc = Now.AddSeconds(10) };
		var keeper = new HoldKeeper(host.Ask);

		pump.Run(keeper.Tick(Now));

		Assert.Equal(0, host.Takes);
	});

	/// <summary>
	/// The closing release is unconditional. A hold the keeper has lost track of is exactly the one
	/// that leaves somebody's application stopped, and asking to release one that is not there is
	/// answered rather than refused.
	/// </summary>
	[Test]
	public void Releasing_asks_even_when_it_believes_it_holds_nothing() => OnOneThread(pump =>
	{
		var host = new Host();
		var keeper = new HoldKeeper(host.Ask);

		pump.Run(keeper.ReleaseAsync());

		Assert.Equal(1, host.Releases);
		Assert.False(keeper.Held);
	});

	/// <summary>
	/// Runs a body with a pump installed as the synchronization context, the way a window runs. The
	/// previous context is put back, so a test cannot change how the ones after it behave.
	/// </summary>
	private static void OnOneThread(Action<Pump> body)
	{
		var previous = SynchronizationContext.Current;
		var pump = new Pump();

		SynchronizationContext.SetSynchronizationContext(pump);

		try
		{
			body(pump);
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(previous);
		}
	}

	/// <summary>
	/// A message loop with no thread of its own: continuations queue, and run when a test asks for
	/// them. That is what a window's dispatcher does, and it is what makes a batch of readers one
	/// decision rather than a race.
	/// </summary>
	private sealed class Pump : SynchronizationContext
	{
		private readonly Queue<(SendOrPostCallback Callback, object? State)> _queued = new();

		public override void Post(SendOrPostCallback callback, object? state) => _queued.Enqueue((callback, state));

		public override void Send(SendOrPostCallback callback, object? state) => callback(state);

		/// <summary>Runs what is queued until the work finishes, and rethrows what it threw.</summary>
		public void Run(Task work)
		{
			while (!work.IsCompleted && _queued.Count > 0)
			{
				var (callback, state) = _queued.Dequeue();
				callback(state);
			}

			// Nothing left to run and still not finished means it is waiting on something no test can
			// produce. Saying so beats blocking, which would present as a suite that hangs.
			Assert.True(work.IsCompleted, "The work is waiting on something this pump cannot run.");

			work.GetAwaiter().GetResult();
		}
	}

	/// <summary>Stands in for the host, counting what it was asked and answering as the host does.</summary>
	private sealed class Host
	{
		public int Takes { get; private set; }

		public int Releases { get; private set; }

		public bool Stopped { get; set; } = true;

		public DateTime DeadlineUtc { get; set; } = Now.AddSeconds(120);

		public Exception? Throws { get; set; }

		public Task<LiveHoldResult> Ask(int? seconds, bool release, CancellationToken cancellationToken)
		{
			if (release) Releases++;
			else Takes++;

			if (Throws is { } failure) return Task.FromException<LiveHoldResult>(failure);

			if (!Stopped)
			{
				return Task.FromResult(new LiveHoldResult
				{
					Execution = LiveExecutionState.Running,
					Applied = false,
					Detail = "Nothing is stopped, so there is no stop to hold.",
				});
			}

			return Task.FromResult(new LiveHoldResult
			{
				Execution = LiveExecutionState.StoppedAtBreakpoint,
				Applied = true,
				Stop = new LiveStop
				{
					State = LiveExecutionState.StoppedAtBreakpoint,
					EventSequence = 7,
					StoppedAtUtc = Now,
					Resume = LiveStopResume.HeldByOperator,
					ResumeDeadlineUtc = DeadlineUtc,
				},
			});
		}
	}
}
