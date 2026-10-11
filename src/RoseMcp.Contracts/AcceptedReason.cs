namespace RoseMcp.Contracts;

/// <summary>One kind of reason accepted for one project, or for one subject of a reason naming no project.</summary>
public sealed record AcceptedReason
{
	/// <summary>The kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>What it covers: a project by name, or, for a reason that names no project, its subject -- the assembly that would not load, the platform chosen.</summary>
	public string Covering { get; init; } = string.Empty;

	/// <summary>When it was accepted.</summary>
	public required DateTime AcceptedUtc { get; init; }

	/// <summary>What agents read about it.</summary>
	public required AgentsRead AgentsRead { get; init; }

	/// <summary>The person's note, where agents read one.</summary>
	public string? Note { get; init; }
}
