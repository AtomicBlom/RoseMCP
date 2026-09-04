namespace RoseMcp.Worker;

/// <summary>What a set of solution changes did, or would do.</summary>
public sealed record WriteOutcome
{
	public required IReadOnlyList<string> ChangedFiles { get; init; }

	public required string Diff { get; init; }

	/// <summary>
	/// What the write did that <see cref="Diff"/> cannot show. Empty almost always; the case it exists
	/// for is a line-ending rewrite, which changes no line's content and so produces no hunk.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
