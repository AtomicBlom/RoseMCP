namespace RoseMcp.Contracts;

/// <summary>What a name could be, and whether that is a single answer.</summary>
public sealed record NameResolutionResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>The name that was searched for, after stripping any qualification and type arguments.</summary>
	public required string Name { get; init; }

	/// <summary>
	/// Every namespace that would bring something of this name into scope, usable ones first.
	/// </summary>
	public required IReadOnlyList<NameCandidate> Candidates { get; init; }

	/// <summary>
	/// The one namespace to import, set only when exactly one candidate would actually resolve the
	/// name. Null for none and null for several -- deliberately the same shape, because the caller
	/// has to look at the candidates in both cases and a first-of-several would be a guess dressed
	/// up as an answer. That guess is the failure this tool exists to avoid: the wrong import
	/// compiles and binds to the wrong type.
	/// </summary>
	public string? Import { get; init; }

	public int TotalCount { get; init; }

	public bool Truncated { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}
