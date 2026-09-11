namespace RoseMcp.Contracts;

/// <summary>
/// What a search over a target's loaded modules found for a typed query, best first.
/// <para>
/// It reads module files rather than the target, so it answers whether or not the target is stopped
/// and costs it no interruption. That is what makes it usable as an autocomplete at all.
/// </para>
/// </summary>
public sealed record LiveMethodMatches
{
	/// <summary>The query as asked, so an answer arriving after the text moved on can be discarded.</summary>
	public required string Query { get; init; }

	/// <summary>The best matches, best first.</summary>
	public required IReadOnlyList<LiveMethodMatch> Matches { get; init; }

	/// <summary>
	/// How many methods matched altogether, which is how a reader knows the list is the top of
	/// something rather than all of it.
	/// </summary>
	public required int Total { get; init; }

	/// <summary>How many of the target's modules were read.</summary>
	public required int ModulesSearched { get; init; }

	/// <summary>
	/// What a reader needs to know about the answer that the list itself does not say: a query too
	/// short to run, a session with no modules recorded yet, modules that could not be read.
	/// </summary>
	public string? Detail { get; init; }
}
