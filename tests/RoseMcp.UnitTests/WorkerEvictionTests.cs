using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which workers the eviction sweep stops. Every case that keeps a worker is a case where stopping
/// it would fail a call somebody is waiting on, or throw away a load somebody is about to use; every
/// case that stops one is memory nobody can reach.
/// </summary>
public sealed class WorkerEvictionTests
{
	private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

	private static EvictionFacts Idle(TimeSpan idleFor) => new(
		Alive: true,
		Loading: false,
		Busy: false,
		LastUsedUtc: Now - idleFor,
		StoppedUtc: null,
		SolutionMissingSinceUtc: null);

	private static EvictionVerdict Decide(EvictionFacts facts) => WorkerEviction.Decide(facts, IdleAfter, Grace, Now);

	[Test]
	public void A_worker_used_within_the_limit_is_kept() =>
		Decide(Idle(TimeSpan.FromMinutes(29))).ShouldBe(EvictionVerdict.Keep);

	[Test]
	public void A_worker_unused_past_the_limit_is_evicted() =>
		Decide(Idle(TimeSpan.FromMinutes(30))).ShouldBe(EvictionVerdict.EvictIdle);

	/// <summary>
	/// A call holding the worker, or an operation running on it, means somebody is waiting on it --
	/// however long ago the last call ended.
	/// </summary>
	[Test]
	public void A_busy_worker_is_kept_however_long_it_has_been_idle() =>
		Decide(Idle(TimeSpan.FromHours(5)) with { Busy = true }).ShouldBe(EvictionVerdict.Keep);

	/// <summary>A load is the expensive part; stopping one half-way wastes it and the next call pays again.</summary>
	[Test]
	public void A_loading_worker_is_kept() =>
		Decide(Idle(TimeSpan.FromHours(1)) with { Loading = true }).ShouldBe(EvictionVerdict.Keep);

	/// <summary>
	/// A branch switch removes and restores a solution file in seconds, and the worker rides it out
	/// on its last snapshot; only a solution gone past the grace period is a removed worktree.
	/// </summary>
	[Test]
	public void A_solution_missing_within_the_grace_period_keeps_its_worker() =>
		Decide(Idle(TimeSpan.FromMinutes(1)) with { SolutionMissingSinceUtc = Now - TimeSpan.FromMinutes(1) })
			.ShouldBe(EvictionVerdict.Keep);

	[Test]
	public void A_solution_missing_past_the_grace_period_retires_its_worker_before_it_goes_idle() =>
		Decide(Idle(TimeSpan.FromMinutes(1)) with { SolutionMissingSinceUtc = Now - TimeSpan.FromMinutes(3) })
			.ShouldBe(EvictionVerdict.EvictSolutionGone);

	[Test]
	public void A_busy_worker_is_kept_even_with_its_solution_gone() =>
		Decide(Idle(TimeSpan.FromMinutes(1)) with { Busy = true, SolutionMissingSinceUtc = Now - TimeSpan.FromHours(1) })
			.ShouldBe(EvictionVerdict.Keep);

	/// <summary>
	/// A stopped worker's row is what tells a person it was evicted and why, so it stays for as long
	/// as an idle worker would, and then goes rather than accumulating one row per removed worktree.
	/// </summary>
	[Test]
	public void A_stopped_worker_keeps_its_row_for_the_idle_limit_then_is_forgotten()
	{
		var stopped = Idle(TimeSpan.FromHours(2)) with { Alive = false, StoppedUtc = Now - TimeSpan.FromMinutes(10) };

		Decide(stopped).ShouldBe(EvictionVerdict.Keep);
		Decide(stopped with { StoppedUtc = Now - TimeSpan.FromMinutes(30) }).ShouldBe(EvictionVerdict.Forget);
	}

	[Test]
	public void A_stopped_worker_is_never_evicted_again()
	{
		var stopped = Idle(TimeSpan.FromHours(2)) with
		{
			Alive = false,
			StoppedUtc = Now,
			SolutionMissingSinceUtc = Now - TimeSpan.FromHours(1),
		};

		Decide(stopped).ShouldBe(EvictionVerdict.Keep);
	}

	/// <summary>The reason is read later by someone who finds the workspace cold, so it says how long and what happens next.</summary>
	[Test]
	public void An_idle_eviction_says_how_long_and_what_the_next_call_does()
	{
		var reason = WorkerEviction.Explain(EvictionVerdict.EvictIdle, Idle(TimeSpan.FromMinutes(31)), IdleAfter, Now);

		reason.ShouldContain("31 min");
		reason.ShouldContain("30 min limit");
		reason.ShouldContain("next call");
	}

	[Test]
	public void A_solution_gone_eviction_says_how_long_it_has_been_missing()
	{
		var facts = Idle(TimeSpan.FromMinutes(5)) with { SolutionMissingSinceUtc = Now - TimeSpan.FromMinutes(3) };

		var reason = WorkerEviction.Explain(EvictionVerdict.EvictSolutionGone, facts, IdleAfter, Now);

		reason.ShouldContain("missing for 3 min");
		reason.ShouldContain("unused for 5 min");
	}

	[Test]
	public void Only_an_eviction_has_a_reason_to_explain() =>
		Should.Throw<ArgumentOutOfRangeException>(
			() => WorkerEviction.Explain(EvictionVerdict.Keep, Idle(TimeSpan.Zero), IdleAfter, Now));

	[Test]
	[Arguments(45, "45 s")]
	[Arguments(60, "1 min")]
	[Arguments(31 * 60 + 59, "31 min")]
	[Arguments(2 * 3600 + 5 * 60, "2 h 5 min")]
	[Arguments(-5, "0 s")]
	public void Durations_read_as_a_person_would_say_them(int seconds, string expected) =>
		WorkerEviction.Duration(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

	/// <summary>
	/// The eviction is filed as a finished row with its reason, so the tray and the admin endpoint
	/// show it beside the worker's last operations after the process is gone.
	/// </summary>
	[Test]
	public void A_note_is_filed_as_a_finished_operation_with_its_reason()
	{
		var log = new ActivityLog();
		var solution = @"D:\somewhere\Thing.sln";

		log.Note(solution, WorkspaceManager.EvictOperation, "Unused for 31 min.");

		log.Running(solution).ShouldBeEmpty();

		var noted = log.Recent(solution).ShouldHaveSingleItem();

		noted.Operation.ShouldBe(WorkspaceManager.EvictOperation);
		noted.Message.ShouldBe("Unused for 31 min.");
		noted.Outcome.ShouldBe(ActivityOutcome.Succeeded);
	}
}
