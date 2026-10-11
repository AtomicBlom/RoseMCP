namespace RoseMcp.Contracts;

/// <summary>An agent's answer to a note, through <c>rose_reply</c>, as the window shows it under the note.</summary>
public sealed record NoteReply
{
	/// <summary>What the agent said, as it said it, cut to the length a note's text is.</summary>
	public required string Text { get; init; }

	/// <summary>When it answered, on the broker's clock.</summary>
	public required DateTimeOffset RepliedUtc { get; init; }

	/// <summary>The activity record's id for the <c>rose_reply</c> call, where the call was recorded.</summary>
	public string? ActivityEntry { get; init; }
}
