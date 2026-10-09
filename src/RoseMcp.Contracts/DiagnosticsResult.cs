namespace RoseMcp.Contracts;

/// <summary>Diagnostics for one snapshot, with the revision they describe.</summary>
public sealed record DiagnosticsResult : WorkspaceScopedResult
{
	/// <summary>The snapshot these diagnostics were computed against.</summary>
	public required long Revision { get; init; }

	/// <summary>The diagnostics found, or none where there were more than <c>maxResults</c> and <see cref="Shape"/> describes them instead.</summary>
	public required IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; }

	/// <summary>How many matched, listed or not.</summary>
	public required int TotalCount { get; init; }

	/// <summary>
	/// True where there were more than <c>maxResults</c>, so they are described by <see cref="Shape"/>
	/// rather than listed. Raising <c>maxResults</c> to <see cref="TotalCount"/> lists them; narrowing
	/// lists fewer.
	/// </summary>
	public required bool Truncated { get; init; }

	/// <summary>How the diagnostics divide, given where there were too many to list.</summary>
	public DiagnosticShape? Shape { get; init; }

	/// <summary>
	/// Whether analyzers ran. Compiler-only results can be perfectly clean while analyzers would
	/// have plenty to say, so this has to be visible rather than assumed.
	/// </summary>
	public required bool IncludedAnalyzers { get; init; }

	/// <summary>Reconciliation notices and anything that went wrong while analysing.</summary>
	public required IReadOnlyList<string> Notices { get; init; }
}
