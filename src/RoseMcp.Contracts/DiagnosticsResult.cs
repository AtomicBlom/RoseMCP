namespace RoseMcp.Contracts;

/// <summary>Diagnostics for one snapshot, with the revision they describe.</summary>
public sealed record DiagnosticsResult
{
	/// <summary>The snapshot these diagnostics were computed against.</summary>
	public required long Revision { get; init; }

	public required IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; }

	/// <summary>How many matched before <c>maxResults</c> was applied.</summary>
	public required int TotalCount { get; init; }

	/// <summary>True when results were cut short, so a caller knows the list is not exhaustive.</summary>
	public required bool Truncated { get; init; }

	/// <summary>
	/// Whether analyzers ran. Compiler-only results can be perfectly clean while analyzers would
	/// have plenty to say, so this has to be visible rather than assumed.
	/// </summary>
	public required bool IncludedAnalyzers { get; init; }

	/// <summary>Reconciliation notices and anything that went wrong while analysing.</summary>
	public required IReadOnlyList<string> Notices { get; init; }
}
