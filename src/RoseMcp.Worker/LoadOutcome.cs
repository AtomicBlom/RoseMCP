using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// What the last load of this solution cost and how it went, kept so status can still say.
/// <para>
/// Status re-describes the solution from the live snapshot every time, which is what makes its
/// counts honest about disk. These three facts are not properties of the snapshot though -- they
/// belong to the load that produced it -- so re-describing dropped them, and every status answer
/// reported no restore, no load diagnostics and a load time of zero.
/// </para>
/// <para>
/// That mattered beyond the missing numbers. A failed restore reaches <c>degradedReasons</c> only
/// through <see cref="Restore"/>, so with nothing to read the workspace called itself healthy in
/// exactly the situation it exists to warn about: analyzers and generators silently absent because
/// their packages were never restored.
/// </para>
/// </summary>
public sealed record LoadOutcome
{
	/// <summary>How long the design-time build took, in seconds.</summary>
	public required double Seconds { get; init; }

	/// <summary>Whether restore ran and whether it worked, or null if it was never attempted.</summary>
	public required RestoreReport? Restore { get; init; }

	/// <summary>What MSBuild complained about while opening. Empty is the good case.</summary>
	public required IReadOnlyList<WorkspaceDiagnostic> Diagnostics { get; init; }

	/// <summary>
	/// Snapshots what the load produced. Copied rather than referenced because the workspace it
	/// came from is disposed on the next reload, and a status call is not going to be holding the
	/// lock that would make reading a live collection safe.
	/// </summary>
	public static LoadOutcome From(LoadResult load) => new()
	{
		Seconds = load.Report.LoadSeconds,
		Restore = load.Report.Restore,
		Diagnostics = [.. load.Workspace.Diagnostics],
	};
}
