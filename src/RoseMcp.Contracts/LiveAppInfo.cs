namespace RoseMcp.Contracts;

/// <summary>
/// What a live-app host can say about itself and its target, cheaply. The broker asks on connect,
/// the way it asks a worker for <see cref="WorkerInfo"/>, so it learns the host's process id and the
/// architecture it launched as without waiting on any real work.
/// </summary>
public sealed record LiveAppInfo
{
	/// <summary>The host process's own id (not the target's).</summary>
	public required int HostProcessId { get; init; }

	/// <summary>The architecture the host launched as, which is the target's architecture.</summary>
	public required TargetArchitecture Architecture { get; init; }

	public required LiveAppSessionState State { get; init; }

	/// <summary>The target process id, once known.</summary>
	public int? TargetProcessId { get; init; }

	/// <summary>Why the session is faulted, when it is.</summary>
	public string? Detail { get; init; }
}
