namespace RoseMcp.Contracts;

/// <summary>One running worker's memory, named by the workspace it holds.</summary>
public sealed record WorkerMemory
{
	/// <summary>The solution the worker holds, absolute.</summary>
	public required string Workspace { get; init; }

	/// <summary>The short key of <see cref="Workspace"/>.</summary>
	public required string WorkspaceKey { get; init; }

	/// <summary>The worker process, or null where it could not be read: gone between the listing and the sample.</summary>
	public ProcessMemory? Process { get; init; }
}
