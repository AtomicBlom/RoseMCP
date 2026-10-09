namespace RoseMcp.Broker;

/// <summary>What the eviction sweep does with one worker.</summary>
public enum EvictionVerdict
{
	/// <summary>Leave it as it is.</summary>
	Keep,

	/// <summary>Stop it: nothing has used it for longer than the idle limit.</summary>
	EvictIdle,

	/// <summary>Stop it: its solution file has been missing for longer than the grace period.</summary>
	EvictSolutionGone,

	/// <summary>
	/// Drop the row of a worker that stopped longer ago than the idle limit. Its process is already
	/// gone; this is the record of it, kept that long so a person can read why it stopped.
	/// </summary>
	Forget,
}
