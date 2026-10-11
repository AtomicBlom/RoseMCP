namespace RoseMcp.Contracts;

/// <summary>
/// An agent session named on a debug session: the one that started it, the one a person shared theirs with, or the
/// one that set the breakpoint it is stopped at. A window names it by its row in the client table, which is what a
/// note to it and a share with it take.
/// </summary>
public sealed record LiveAppAgent
{
	/// <summary>The client's row in the broker's client table, by the id <c>/operator/clients</c> lists it under.</summary>
	public required string ClientId { get; init; }

	/// <summary>What the client is called: its label, its folder or its name, as the table had it when it was named here.</summary>
	public string? Name { get; init; }

	/// <summary>
	/// Whether the client table lists the client now, where the reader can know; null on an agent's own list. A share
	/// outlives its client's row, since a relay that reconnects is the same session and reaches it again.
	/// </summary>
	public bool? Connected { get; init; }
}
