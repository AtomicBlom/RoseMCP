namespace RoseMcp.Broker;

/// <summary>
/// Decides when a worker holding a rebuilt analyzer is replaced by a fresh one, for a person who has asked
/// for that, and says why in words a person reads afterwards.
/// <para>
/// A pure function over a worker's facts and the clock, apart from the manager that acts on it, the way
/// <see cref="WorkerEviction"/> is, so the rule is tested without a worker going quiet for a real minute.
/// </para>
/// </summary>
public static class RebuiltAnalyzerReload
{
	/// <summary>
	/// Whether to reload now: a live worker whose reads found a rebuilt analyzer, that nobody holds or is
	/// loading, and that has gone unused for <paramref name="quietFor"/> since both its last use and the
	/// broker hearing of the rebuild. Unused rather than straight away, because a reload is a design-time build
	/// of every project, and starting one under an agent mid-task makes its next calls wait for it.
	/// </summary>
	public static bool Due(EvictionFacts facts, TimeSpan quietFor, DateTime nowUtc)
	{
		if (facts.RebuiltAnalyzersSinceUtc is not { } heard) return false;
		if (!facts.Alive || facts.Busy || facts.Loading) return false;

		var quietSince = heard > facts.LastUsedUtc ? heard : facts.LastUsedUtc;

		return nowUtc - quietSince >= quietFor;
	}

	/// <summary>
	/// Why a worker was replaced, for the activity log of the one replacing it. Written for the person who
	/// finds the workspace loading again with nobody having asked.
	/// </summary>
	public static string Explain(string? rebuilt, EvictionFacts facts, DateTime nowUtc) =>
		$"Reloaded after {WorkerEviction.Duration(nowUtc - facts.LastUsedUtc)} unused, because an analyzer it had "
			+ "loaded was rebuilt and the setting to reload rebuilt analyzers when idle is on. "
			+ (rebuilt ?? string.Empty);
}
