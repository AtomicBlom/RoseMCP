namespace RoseMcp.Contracts;

/// <summary>
/// An analyzer or source-generator assembly that MSBuild passes to the compiler and that will not
/// load, so everything inside it produces nothing.
/// <para>
/// Structured rather than prose because <see cref="WorkspaceStatusReport.DegradedReasons"/> folds
/// these into a single line and the fold groups by assembly. A reason that had to be parsed back
/// apart to group it would be carrying one fact in two shapes, and the shape meant for reading is
/// the wrong one to compute from.
/// </para>
/// </summary>
public sealed record AnalyzerLoadFailure
{
	/// <summary>
	/// File name of the assembly. The name rather than the full path, because the failure people hit
	/// is several versions of one generator arriving from different target packs, and the file name
	/// is what makes them one group.
	/// </summary>
	public required string Assembly { get; init; }

	/// <summary>What the runtime said, which carries the version it wanted and the HRESULT.</summary>
	public required string Message { get; init; }
}
