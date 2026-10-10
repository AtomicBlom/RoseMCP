using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// When a worker holding a rebuilt analyzer is replaced, for a person who has asked for that. Every case
/// that waits is a case where reloading would make somebody's next call wait on a design-time build, or
/// fail the call already holding the worker.
/// </summary>
public sealed class RebuiltAnalyzerReloadTests
{
	private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(1);

	private static EvictionFacts Rebuilt(TimeSpan heardAgo, TimeSpan usedAgo) => new(
		Alive: true,
		Loading: false,
		Busy: false,
		LastUsedUtc: Now - usedAgo,
		StoppedUtc: null,
		SolutionMissingSinceUtc: null,
		RebuiltAnalyzersSinceUtc: Now - heardAgo);

	private static bool Due(EvictionFacts facts) => RebuiltAnalyzerReload.Due(facts, QuietFor, Now);

	[Test]
	public void A_worker_unused_for_a_minute_after_the_rebuild_is_reloaded() =>
		Due(Rebuilt(heardAgo: TimeSpan.FromMinutes(2), usedAgo: TimeSpan.FromMinutes(1))).ShouldBeTrue();

	[Test]
	public void A_worker_used_within_the_minute_waits() =>
		Due(Rebuilt(heardAgo: TimeSpan.FromMinutes(5), usedAgo: TimeSpan.FromSeconds(59))).ShouldBeFalse();

	/// <summary>
	/// Heard of after the last use -- by a refresh that lands after its call ended -- the minute counts from
	/// hearing, so the reload never lands sooner than a minute after anyone could have learned of it.
	/// </summary>
	[Test]
	public void The_minute_counts_from_hearing_of_the_rebuild_when_that_is_later() =>
		Due(Rebuilt(heardAgo: TimeSpan.FromSeconds(30), usedAgo: TimeSpan.FromMinutes(10))).ShouldBeFalse();

	[Test]
	public void A_worker_with_nothing_rebuilt_is_never_reloaded() =>
		Due(Rebuilt(TimeSpan.FromHours(1), TimeSpan.FromHours(1)) with { RebuiltAnalyzersSinceUtc = null }).ShouldBeFalse();

	/// <summary>A call holding the worker, or an operation running on it, is somebody waiting on it.</summary>
	[Test]
	public void A_busy_worker_is_not_reloaded() =>
		Due(Rebuilt(TimeSpan.FromHours(1), TimeSpan.FromHours(1)) with { Busy = true }).ShouldBeFalse();

	[Test]
	public void A_loading_worker_is_not_reloaded() =>
		Due(Rebuilt(TimeSpan.FromHours(1), TimeSpan.FromHours(1)) with { Loading = true }).ShouldBeFalse();

	[Test]
	public void A_stopped_worker_is_not_reloaded() =>
		Due(Rebuilt(TimeSpan.FromHours(1), TimeSpan.FromHours(1)) with { Alive = false, StoppedUtc = Now }).ShouldBeFalse();

	[Test]
	public void The_reason_says_how_long_it_sat_and_which_assembly()
	{
		var facts = Rebuilt(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2));

		var reason = RebuiltAnalyzerReload.Explain("Gen.dll was rebuilt after this worker loaded it.", facts, Now);

		reason.ShouldContain("2 min unused", Case.Sensitive);
		reason.ShouldContain("Gen.dll", Case.Sensitive);
	}
}
