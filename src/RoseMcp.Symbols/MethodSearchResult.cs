namespace RoseMcp.Symbols;

/// <summary>What a search over a set of modules found, and how much of them it could read.</summary>
public sealed record MethodSearchResult
{
	/// <summary>The best matches, best first, no more than the limit asked for.</summary>
	public required IReadOnlyList<MethodCandidate> Matches { get; init; }

	/// <summary>
	/// How many methods matched in total, which is how the caller knows the list is the top of
	/// something rather than all of it.
	/// </summary>
	public required int Total { get; init; }

	/// <summary>How many modules were read.</summary>
	public required int ModulesSearched { get; init; }

	/// <summary>
	/// How many could not be, which is an ordinary number rather than a fault: a target loads native
	/// libraries, and a module built in memory has no file to read.
	/// </summary>
	public required int ModulesUnreadable { get; init; }
}
