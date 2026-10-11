namespace RoseMcp.Contracts;

/// <summary>
/// What Rose holds in memory, process by process, and what the apps it debugs and the machine hold beside it, as
/// <c>GET /operator/memory</c> answers: the bar along the bottom of every view.
/// <para>
/// Sampled when it is asked for and not kept: every figure differs from one read to the next, so there is no
/// version to wait past, and a window reads it on a timer.
/// </para>
/// </summary>
public sealed record MemoryReport
{
	/// <summary>When the sample was taken.</summary>
	public required DateTime SampledUtc { get; init; }

	/// <summary>
	/// The broker's own process. Where the tray hosts the broker this is the tray process, its icon included,
	/// since the two are one process.
	/// </summary>
	public required ProcessMemory Broker { get; init; }

	/// <summary>Each running worker, one per loaded solution, ordered by solution path. A stopped worker holds nothing.</summary>
	public IReadOnlyList<WorkerMemory> Workers { get; init; } = [];

	/// <summary>Each debug session's host, and the app it debugs apart from it.</summary>
	public IReadOnlyList<LiveAppMemory> LiveApps { get; init; } = [];

	/// <summary>The machine's physical memory; null where it could not be read.</summary>
	public MachineMemory? Machine { get; init; }
}
