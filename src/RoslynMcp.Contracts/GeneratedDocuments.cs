namespace RoslynMcp.Contracts;

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

public sealed record GeneratedDocumentSummary
{
	public required string Project { get; init; }

	/// <summary>The generator's own name for the file, and the handle used to read it back.</summary>
	public required string HintName { get; init; }

	/// <summary>Synthetic path Roslyn assigns the generated tree. Nothing exists there on disk.</summary>
	public required string FilePath { get; init; }

	public required int LineCount { get; init; }

	public required int CharacterCount { get; init; }
}

/// <summary>The text of one generated document.</summary>
public sealed record GeneratedDocumentContent
{
	public required long Revision { get; init; }

	public required string Project { get; init; }

	public required string HintName { get; init; }

	public required string Text { get; init; }
}
