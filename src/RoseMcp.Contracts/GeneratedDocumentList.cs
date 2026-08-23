namespace RoseMcp.Contracts;

/// <summary>Source-generated documents in one project.</summary>
public sealed record GeneratedDocumentList
{
	public required long Revision { get; init; }

	public required IReadOnlyList<GeneratedDocumentSummary> Documents { get; init; }

	/// <summary>
	/// Populated when a project has generators but produced nothing, or has no generators at all.
	/// An empty list is ambiguous on its own and that ambiguity is exactly where the usual silent
	/// failure hides.
	/// </summary>
	public required IReadOnlyList<string> Notices { get; init; }
}
