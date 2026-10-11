namespace RoseMcp.Contracts;

/// <summary>The machine's physical memory, so a window can set what Rose holds against what there is.</summary>
public sealed record MachineMemory
{
	/// <summary>All the physical memory the machine has, or the limit a container sets on this process.</summary>
	public required long TotalBytes { get; init; }

	/// <summary>
	/// How much of it is free for a new process to take, cache it can give back included. Null where the platform
	/// does not say.
	/// </summary>
	public long? AvailableBytes { get; init; }
}
