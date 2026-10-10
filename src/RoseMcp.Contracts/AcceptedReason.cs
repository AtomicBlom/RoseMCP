namespace RoseMcp.Contracts;

/// <summary>One kind of reason accepted for one project, or for the whole solution where the project is empty.</summary>
public sealed record AcceptedReason
{
	/// <summary>The kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>The project, by name; empty for the whole solution.</summary>
	public string Project { get; init; } = string.Empty;

	/// <summary>When it was accepted.</summary>
	public required DateTime AcceptedUtc { get; init; }

	/// <summary>What agents read about it.</summary>
	public required AgentsRead AgentsRead { get; init; }

	/// <summary>The person's note, where agents read one.</summary>
	public string? Note { get; init; }
}
