namespace RoseMcp.Contracts;

/// <summary>The text of one generated document.</summary>
public sealed record GeneratedDocumentContent : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required string Project { get; init; }

	public required string HintName { get; init; }

	public required string Text { get; init; }

	/// <summary>
	/// What the read barrier found on the way, as every read says it: that the workspace is degraded, or that
	/// the generator which wrote this text was rebuilt after the worker loaded it, so this is the old build's
	/// output -- the one read where that matters most.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
