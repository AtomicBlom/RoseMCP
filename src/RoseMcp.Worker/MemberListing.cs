using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// One type's members as listed, with what the listing left out: how many matched, whether the cap
/// stopped it, and the notices that say so -- since a short or empty list otherwise reads as the
/// whole answer.
/// </summary>
public sealed record MemberListing
{
	/// <summary>The members listed, at most the cap, in the order the type declares them.</summary>
	public required IReadOnlyList<OutlinedMember> Members { get; init; }

	/// <summary>How many members matched the name filter, listed or not.</summary>
	public required int Total { get; init; }

	/// <summary>True where the cap stopped the listing before every match was listed.</summary>
	public required bool Truncated { get; init; }

	/// <summary>What the name filter or the cap left out, said; empty where neither changed the listing.</summary>
	public required IReadOnlyList<string> Notices { get; init; }
}
