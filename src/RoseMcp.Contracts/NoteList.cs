namespace RoseMcp.Contracts;

/// <summary>
/// The notes the broker holds, as <c>GET /operator/notes</c> answers, with the version of the view that a long poll
/// waits past.
/// </summary>
public sealed record NoteList
{
	/// <summary>Every note asked for, the earliest sent first.</summary>
	public IReadOnlyList<AgentNote> Notes { get; init; } = [];

	/// <summary>
	/// What the view held when it was read, as an opaque string: passed back as <c>after</c> with a
	/// <c>waitSeconds</c>, the next read waits until the view holds something else.
	/// </summary>
	public required string Version { get; init; }
}
