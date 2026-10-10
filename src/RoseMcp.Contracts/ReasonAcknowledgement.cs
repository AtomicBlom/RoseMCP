namespace RoseMcp.Contracts;

/// <summary>One reason <c>rose_workspace_acknowledge</c> was given, and what became of it.</summary>
public sealed record ReasonAcknowledgement
{
	/// <summary>The id as the caller sent it.</summary>
	public required string Reason { get; init; }

	/// <summary>What happened.</summary>
	public required AcknowledgementOutcome Status { get; init; }

	/// <summary>The reason's kind, where the id named one.</summary>
	public string? Kind { get; init; }

	/// <summary>The projects the acknowledgement or the proposal covers; empty for the whole solution.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>The proposal waiting for the person, where one was made.</summary>
	public string? ProposalId { get; init; }

	/// <summary>Why nothing was done, where nothing was: which ids there are instead, or that it is already accepted.</summary>
	public string? Detail { get; init; }
}
