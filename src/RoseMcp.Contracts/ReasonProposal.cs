namespace RoseMcp.Contracts;

/// <summary>
/// An agent's proposal that the person accept a reason, waiting for the person to confirm or reject it.
/// <para>
/// A proposal rather than an acceptance because an acceptance outlives the session that made it and changes
/// what every other agent reads, and nothing an agent says should do that on its own. It is held in memory
/// by the broker and goes with the session that proposed it, so a session that ended leaves nothing behind
/// for the person to wade through.
/// </para>
/// </summary>
public sealed record ReasonProposal : WorkspaceScopedResult
{
	/// <summary>What the operator routes take to confirm or reject it.</summary>
	public required string Id { get; init; }

	/// <summary>The reason's kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>The projects an acceptance would cover; empty for the whole solution.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>Rose's line for the reason, so the person can see what is proposed without asking status.</summary>
	public required string Line { get; init; }

	/// <summary>What the agent proposes agents read about it once accepted.</summary>
	public required AgentsRead AgentsRead { get; init; }

	/// <summary>The note the agent proposes, where it proposed one.</summary>
	public string? Note { get; init; }

	/// <summary>When it was proposed.</summary>
	public required DateTime ProposedUtc { get; init; }
}
