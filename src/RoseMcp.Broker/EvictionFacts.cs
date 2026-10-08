namespace RoseMcp.Broker;

/// <summary>
/// The facts about one worker the sweep decides on, read at one moment.
/// </summary>
/// <param name="Alive">Whether its process is still serving.</param>
/// <param name="Loading">Whether its solution is still loading.</param>
/// <param name="Busy">Whether a call holds it or an operation is running on it.</param>
/// <param name="LastUsedUtc">When a tool call last finished with it, or when it started.</param>
/// <param name="StoppedUtc">When it stopped serving, for a worker that has.</param>
/// <param name="SolutionMissingSinceUtc">When its solution file was first seen missing, if it is.</param>
public readonly record struct EvictionFacts(
	bool Alive,
	bool Loading,
	bool Busy,
	DateTime LastUsedUtc,
	DateTime? StoppedUtc,
	DateTime? SolutionMissingSinceUtc);
