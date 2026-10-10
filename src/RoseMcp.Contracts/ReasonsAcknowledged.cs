namespace RoseMcp.Contracts;

/// <summary>
/// What <c>rose_workspace_acknowledge</c> did with each reason it was given, in the order given.
/// <para>
/// One entry per reason, each with its own outcome, because a reason that is gone since the caller read
/// status is that entry's news and no reason to refuse the others. The revision is the status the ids were
/// resolved against, so a caller holding a newer one can tell its ids may have been read from a different
/// load.
/// </para>
/// </summary>
public sealed record ReasonsAcknowledged : WorkspaceScopedResult
{
	/// <summary>The revision of the status the ids were looked up in.</summary>
	public required long Revision { get; init; }

	/// <summary>One per reason the call named, in the order named.</summary>
	public required IReadOnlyList<ReasonAcknowledgement> Reasons { get; init; }
}
