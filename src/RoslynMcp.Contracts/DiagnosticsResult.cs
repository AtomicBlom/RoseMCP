namespace RoslynMcp.Contracts;

/// <summary>How much of the solution to analyse.</summary>
public enum DiagnosticScope
{
	Document,
	Project,
	Solution,
}

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

/// <summary>One diagnostic, located in a way a caller can act on.</summary>
public sealed record DiagnosticEntry
{
	public required string Id { get; init; }

	public required string Severity { get; init; }

	public required string Message { get; init; }

	public required string Project { get; init; }

	/// <summary>
	/// Path of the file the diagnostic is in. For generated code this is the synthetic path Roslyn
	/// gives the generated tree, which exists nowhere on disk.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>One-based, to match what editors and humans use.</summary>
	public int Line { get; init; }

	public int Column { get; init; }

	/// <summary>
	/// Set when the diagnostic is inside source-generated code. Read the file with
	/// roslyn_read_generated_document -- it is not on disk, so ordinary file reads will not find it.
	/// </summary>
	public string? GeneratedHintName { get; init; }

	/// <summary>Documentation link, where the analyzer supplies one.</summary>
	public string? HelpLink { get; init; }
}
