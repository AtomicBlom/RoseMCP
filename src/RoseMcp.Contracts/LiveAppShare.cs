namespace RoseMcp.Contracts;

/// <summary>
/// The one agent session a person has shared their debug session with: it reaches the session through the debug
/// tools and <c>rose_debug_list</c> shows it, until the person stops sharing or the session ends.
/// </summary>
public sealed record LiveAppShare
{
	/// <summary>The client's row in the broker's client table, by the id <c>/operator/clients</c> lists it under.</summary>
	public required string ClientId { get; init; }

	/// <summary>What the client was called when the session was shared with it: its label, its folder or its name.</summary>
	public string? Name { get; init; }

	/// <summary>
	/// Whether the client table lists the client now, where the reader can know; null on an agent's own list. A share
	/// outlives its client's row, since a relay that reconnects is the same session and reaches it again.
	/// </summary>
	public bool? Connected { get; init; }
}
