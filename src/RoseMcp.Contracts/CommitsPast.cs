namespace RoseMcp.Contracts;

/// <summary>How many commits one ref is past the running build, or why that could not be said.</summary>
public sealed record CommitsPast
{
	/// <summary>The ref counted to: <c>HEAD</c> or <c>origin/main</c>.</summary>
	public required string Ref { get; init; }

	/// <summary>
	/// Commits reachable from <see cref="Ref"/> and not from the running build's commit; zero where
	/// the build is that ref or ahead of it. Null when it could not be counted.
	/// </summary>
	public int? Count { get; init; }

	/// <summary>Why <see cref="Count"/> is null, in words. Null whenever there is a count.</summary>
	public string? Unknown { get; init; }
}
