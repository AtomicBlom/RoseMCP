namespace RoseMcp.Contracts;

/// <summary>The text of one generated document.</summary>
public sealed record GeneratedDocumentContent
{
	public required long Revision { get; init; }

	public required string Project { get; init; }

	public required string HintName { get; init; }

	public required string Text { get; init; }
}
