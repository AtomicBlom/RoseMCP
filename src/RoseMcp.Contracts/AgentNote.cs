namespace RoseMcp.Contracts;

/// <summary>
/// A note a person sent an agent session from a window, as <c>/operator/notes</c> answers it: whom it is for, what
/// it is about, what it says, where its delivery has got to and what the agent answered.
/// <para>
/// Held in the broker's memory, so a broker restart forgets every note. The client is named the way a window names
/// it, by the row id of the client table and the hashed key the activity record files its calls under; never by
/// the session name a relay sends, which no answer carries.
/// </para>
/// </summary>
public sealed record AgentNote
{
	/// <summary>The note's id: what <c>rose_reply</c> takes to answer it.</summary>
	public required string Id { get; init; }

	/// <summary>
	/// The client it is for, as <c>/operator/clients</c> lists it: its row now, where the broker still lists the
	/// session, and the row it was sent to otherwise.
	/// </summary>
	public required string ClientId { get; init; }

	/// <summary>The key the activity record files the client's calls under, so its calls can be found across a restart.</summary>
	public required string ClientKey { get; init; }

	/// <summary>
	/// The label whatever started the session gave it, as the table had it when the note was sent: with
	/// <see cref="ClientFolder"/> and <see cref="ClientName"/>, what a window names a session that has gone by.
	/// </summary>
	public string? ClientLabel { get; init; }

	/// <summary>The folder the session works in, as its relay said when the note was sent.</summary>
	public string? ClientFolder { get; init; }

	/// <summary>The client's own name from its <c>clientInfo</c>, as the table had it when the note was sent.</summary>
	public string? ClientName { get; init; }

	/// <summary>What the note is about.</summary>
	public required NoteSubject Subject { get; init; }

	/// <summary>What the person wrote.</summary>
	public required string Text { get; init; }

	/// <summary>When the person sent it, on the broker's clock.</summary>
	public required DateTimeOffset PostedUtc { get; init; }

	/// <summary>Where its delivery has got to.</summary>
	public required NoteDelivery Delivery { get; init; }

	/// <summary>When it was handed to the agent, or null while it has not been.</summary>
	public DateTimeOffset? DeliveredUtc { get; init; }

	/// <summary>The tool whose answer carried it, by the name the client called it by.</summary>
	public string? DeliveredTool { get; init; }

	/// <summary>The activity record's id for the call whose answer carried it, where the call was recorded.</summary>
	public string? DeliveredWith { get; init; }

	/// <summary>What the agent answered, oldest first.</summary>
	public IReadOnlyList<NoteReply> Replies { get; init; } = [];
}
