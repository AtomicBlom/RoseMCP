namespace RoseMcp.Contracts;

/// <summary>
/// A person's acceptance of a reason: that it is known and expected, and what agents read about it.
/// <para>
/// Remembered per solution, per kind and per project, across loads, so a new kind of reason or a project
/// newly failing is not covered by it and still shows.
/// </para>
/// </summary>
public sealed record ReasonAcceptance
{
	/// <summary>When the person accepted it, the earliest where it covers several projects.</summary>
	public required DateTime AcceptedUtc { get; init; }

	/// <summary>What a read from the workspace tells an agent about it.</summary>
	public required AgentsRead AgentsRead { get; init; }

	/// <summary>The person's own words for agents, read in place of Rose's line when <see cref="AgentsRead"/> is <see cref="AgentsRead.Note"/>.</summary>
	public string? Note { get; init; }
}
