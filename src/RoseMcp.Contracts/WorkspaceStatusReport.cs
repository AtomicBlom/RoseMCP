namespace RoseMcp.Contracts;

/// <summary>
/// Everything a caller needs to decide whether to trust this workspace's answers. Deliberately
/// verbose about failure: a workspace that loaded but cannot see its source generators produces
/// confidently wrong results, so those causes are reported rather than left to be inferred from
/// suspiciously empty output.
/// </summary>
public sealed record WorkspaceStatusReport
{
	public required string SolutionPath { get; init; }

	public required WorkspaceState State { get; init; }

	/// <summary>
	/// Monotonic snapshot counter. Bumped by every mutation, absorbed file change, and reload.
	/// Two results carrying the same revision describe the same world.
	/// </summary>
	public required long Revision { get; init; }

	public required IReadOnlyList<ProjectStatus> Projects { get; init; }

	/// <summary>Diagnostics MSBuild reported while loading -- unresolved references, failed projects.</summary>
	public required IReadOnlyList<string> LoadDiagnostics { get; init; }

	/// <summary>
	/// Why this workspace is <see cref="WorkspaceState.Degraded"/>, each paired with the command
	/// that would fix it. Empty when the load is trustworthy.
	/// </summary>
	public required IReadOnlyList<string> DegradedReasons { get; init; }

	public RestoreReport? Restore { get; init; }

	public double LoadSeconds { get; init; }
}
