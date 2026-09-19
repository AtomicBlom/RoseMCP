namespace RoseMcp.Contracts;

/// <summary>
/// Where a type could be cut, and what each piece would take with it.
/// <para>
/// An island is a set of members that would move together. Finding none is an answer and the
/// common one: a type whose members all reach the same state and each other is one thing, and
/// saying so is worth more than a split invented to have something to report.
/// </para>
/// </summary>
public sealed record IslandsResult : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	/// <summary>The type or file this answers about, as it was asked for.</summary>
	public required string Target { get; init; }

	public required IReadOnlyList<TypeIslands> Types { get; init; }

	public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>One type, and the islands in it.</summary>
public sealed record TypeIslands
{
	public required string Name { get; init; }

	public string? Namespace { get; init; }

	/// <summary>
	/// How many members were weighed, so the size of an island can be read against the whole. Four
	/// members out of six is a rename; four out of sixty is a type waiting to be born.
	/// </summary>
	public required int Members { get; init; }

	/// <summary>
	/// The islands, largest first. Empty means the type holds together, which is the finding rather
	/// than the absence of one.
	/// </summary>
	public required IReadOnlyList<Island> Islands { get; init; }

	/// <summary>
	/// Fields so widely read that they separate nothing -- the type's spine. Left out of every
	/// island's state, because a field nearly every member touches says what the type is rather than
	/// which part of it a member belongs to. A healthy type shows one spine and no islands.
	/// </summary>
	public IReadOnlyList<string> Spine { get; init; } = [];
}

/// <summary>A set of members that would move together, and the evidence that they would.</summary>
public sealed record Island
{
	/// <summary>
	/// Why these members are one island, which decides what extracting them looks like.
	/// <para>
	/// <c>state</c>: they read fields nothing else reads. They become a type, and
	/// <see cref="Fields"/> is the state it owns. <c>reach</c>: every path to them runs through
	/// <see cref="Owner"/>. They become that member's private world -- a class, or a file of its
	/// own -- and what makes them a unit is not what they call but what calls them.
	/// </para>
	/// </summary>
	public required string Kind { get; init; }

	/// <summary>
	/// For a <c>reach</c> island, the member every path to the rest runs through. Absent for a
	/// <c>state</c> island, which has no single door.
	/// </summary>
	public string? Owner { get; init; }

	/// <summary>
	/// The owner of the smallest island this one sits wholly inside, where there is one. Islands
	/// nest, because what a member owns contains what the members under it own, and two overlapping
	/// lists with nothing said about them read as a contradiction rather than a choice. Both are
	/// reported because both are real: the larger is the whole world behind one door, the smaller is
	/// the tighter extraction inside it, and which is wanted depends on how far the caller means to
	/// go.
	/// </summary>
	public string? Within { get; init; }

	public required IReadOnlyList<string> Members { get; init; }

	/// <summary>The fields only these members touch, which is the state the island would take.</summary>
	public IReadOnlyList<string> Fields { get; init; } = [];

	/// <summary>
	/// The line ranges it occupies, merged where they run together. This is the half that says what
	/// the work costs: one range lifts out in an afternoon, and the same members scattered over four
	/// ranges a thousand lines apart is a different job that nothing in a member list reveals.
	/// </summary>
	public required IReadOnlyList<string> Spans { get; init; }
}
