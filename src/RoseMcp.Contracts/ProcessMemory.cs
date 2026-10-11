namespace RoseMcp.Contracts;

/// <summary>
/// One process's memory, sampled from the process table when it was asked for, so a process that has stopped
/// answering still reports.
/// </summary>
public sealed record ProcessMemory
{
	public required int ProcessId { get; init; }

	/// <summary>The physical memory the process holds: what Task Manager shows, and what the machine pays.</summary>
	public required long WorkingSetBytes { get; init; }

	/// <summary>The memory committed to the process alone, paged out or not.</summary>
	public required long PrivateMemoryBytes { get; init; }

	/// <summary>
	/// The managed heap, where the process can say: the broker's own, read in place, and a worker's, as it last
	/// reported. Null for a process that does not report one.
	/// </summary>
	public long? ManagedHeapBytes { get; init; }
}
