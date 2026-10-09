namespace RoseMcp.Broker;

/// <summary>
/// Decides which workers the sweep stops, and says why in words a person reads afterwards.
/// <para>
/// A pure function over a worker's facts and the clock, apart from the manager that acts on it, so
/// the rule can be tested without waiting half an hour for a real worker to go idle.
/// </para>
/// </summary>
public static class WorkerEviction
{
	/// <summary>
	/// The verdict for one worker. Work in progress always wins: a busy or loading worker is kept
	/// however long it has been idle or however long its solution has been gone, because stopping it
	/// would fail a call somebody is waiting on, and the next sweep asks again.
	/// </summary>
	public static EvictionVerdict Decide(
		EvictionFacts facts,
		TimeSpan idleAfter,
		TimeSpan solutionGoneGrace,
		DateTime nowUtc)
	{
		if (!facts.Alive)
		{
			var stoppedLongAgo = facts.StoppedUtc is { } stopped && nowUtc - stopped >= idleAfter;
			return stoppedLongAgo ? EvictionVerdict.Forget : EvictionVerdict.Keep;
		}

		if (facts.Busy || facts.Loading) return EvictionVerdict.Keep;

		var solutionGone = facts.SolutionMissingSinceUtc is { } missing && nowUtc - missing >= solutionGoneGrace;
		if (solutionGone) return EvictionVerdict.EvictSolutionGone;

		return nowUtc - facts.LastUsedUtc >= idleAfter ? EvictionVerdict.EvictIdle : EvictionVerdict.Keep;
	}

	/// <summary>
	/// Why a worker was evicted, for the activity log. Written for the person who finds the workspace
	/// cold later, so it says what the next call will do as well as what happened.
	/// </summary>
	public static string Explain(EvictionVerdict verdict, EvictionFacts facts, TimeSpan idleAfter, DateTime nowUtc) =>
		verdict switch
		{
			EvictionVerdict.EvictIdle =>
				$"Unused for {Duration(nowUtc - facts.LastUsedUtc)}, past the {Duration(idleAfter)} limit. "
					+ "The next call on this workspace starts a fresh worker.",
			EvictionVerdict.EvictSolutionGone =>
				$"The solution file has been missing for {Duration(nowUtc - (facts.SolutionMissingSinceUtc ?? nowUtc))}, "
					+ $"and the worker was unused for {Duration(nowUtc - facts.LastUsedUtc)}. "
					+ "A call after the file comes back starts a fresh worker.",
			_ => throw new ArgumentOutOfRangeException(
				nameof(verdict), verdict, "Only an eviction has a reason to explain."),
		};

	/// <summary>A span as a person would say it: whole hours and minutes, or seconds under a minute.</summary>
	public static string Duration(TimeSpan span) => span switch
	{
		{ TotalHours: >= 1 } => $"{(int)span.TotalHours} h {span.Minutes} min",
		{ TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes} min",
		_ => $"{Math.Max(0, (int)span.TotalSeconds)} s",
	};
}
