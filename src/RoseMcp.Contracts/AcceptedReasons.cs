namespace RoseMcp.Contracts;

/// <summary>Every reason a person has accepted for one solution, as the operator routes answer after a change.</summary>
public sealed record AcceptedReasons : WorkspaceScopedResult
{
	/// <summary>One per kind and project accepted.</summary>
	public required IReadOnlyList<AcceptedReason> Accepted { get; init; }
}
