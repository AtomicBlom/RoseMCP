using Microsoft.CodeAnalysis;

namespace RoslynMcp.Worker;

/// <summary>
/// An immutable view of the solution, already reconciled with disk, plus the revision it belongs
/// to. Expensive analysis runs against this off the session's writer, which is safe precisely
/// because Roslyn solutions are immutable.
/// </summary>
public sealed record WorkspaceSnapshot
{
	public required Solution Solution { get; init; }

	/// <summary>The revision this snapshot represents. Two results sharing it describe one world.</summary>
	public required long Revision { get; init; }

	/// <summary>
	/// True when the snapshot could not be fully reconciled -- the solution file has gone missing
	/// and the unload grace period is running, so this is the last known good state rather than
	/// current truth.
	/// </summary>
	public bool Stale { get; init; }

	/// <summary>
	/// Things that happened during reconciliation and that a caller should know about: projects
	/// reloaded, documents dropped, files that could not be read this time.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
