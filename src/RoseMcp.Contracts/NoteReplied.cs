namespace RoseMcp.Contracts;

/// <summary>
/// What <c>rose_reply</c> did: which note it answered, what that note was about, and how many answers the note now
/// holds. A note belongs to the session it was sent to rather than to a workspace, so this names the note and no
/// workspace.
/// </summary>
public sealed record NoteReplied
{
	/// <summary>The note answered, as the call named it.</summary>
	public required string NoteId { get; init; }

	/// <summary>What the note was about, in the words the person's window shows.</summary>
	public required string About { get; init; }

	/// <summary>When the answer was taken, on the broker's clock.</summary>
	public required DateTimeOffset RepliedUtc { get; init; }

	/// <summary>How many answers the note holds, this one included.</summary>
	public required int Replies { get; init; }

	/// <summary>What the caller should know about the answer, and any note the person has sent since.</summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
