namespace RoseMcp.Contracts;

/// <summary>What applying a code fix did.</summary>
public sealed record CodeFixResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required string DiagnosticId { get; init; }

	/// <summary>
	/// The fix that ran, as its author titled it. Reported because a diagnostic can offer several and
	/// they do different things -- "add readonly" and "remove the field" can answer the same warning.
	/// </summary>
	public required string FixTitle { get; init; }

	public required string Scope { get; init; }

	/// <summary>How many occurrences of the diagnostic were found in that scope.</summary>
	public required int Occurrences { get; init; }

	public required IReadOnlyList<string> ChangedFiles { get; init; }

	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	public required string Diff { get; init; }

	public required IReadOnlyList<string> Notices { get; init; }
}
