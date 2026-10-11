namespace RoseMcp.Contracts;

/// <summary>What a worker can say about itself, cheaply and without loading anything.</summary>
public sealed record WorkerInfo
{
	public required int ProcessId { get; init; }

	public required string SolutionPath { get; init; }

	/// <summary>
	/// Managed heap size. Worth reporting alongside the working set the broker samples externally,
	/// because for a Roslyn host the gap between the two is mostly compilation caches.
	/// </summary>
	public required long ManagedHeapBytes { get; init; }

	/// <summary>
	/// The log file this worker is writing, so a reader looking at a workspace can open the log that
	/// explains it rather than guessing which of twenty files in the folder is the one. Null when file
	/// logging could not start.
	/// </summary>
	public string? LogPath { get; init; }

	/// <summary>
	/// Analyzer, generator and code-fix assemblies this worker's reads have found rebuilt on disk since it
	/// loaded them, by full path. Empty until a read finds one, and for as long as none is. A worker cannot
	/// unload an assembly, so these stay until a new worker replaces this one.
	/// </summary>
	public IReadOnlyList<string> RebuiltAnalyzers { get; init; } = [];

	/// <summary>
	/// What every read from this worker says about <see cref="RebuiltAnalyzers"/>, for a broker to show
	/// where a person looks, or null where nothing has been rebuilt. Composed here rather than by the broker,
	/// so the agent and the person read the same sentence.
	/// </summary>
	public string? RebuiltAnalyzersNotice { get; init; }
}
