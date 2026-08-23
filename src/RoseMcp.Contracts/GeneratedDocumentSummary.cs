namespace RoseMcp.Contracts;

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
