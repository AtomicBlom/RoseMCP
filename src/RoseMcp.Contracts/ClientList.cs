namespace RoseMcp.Contracts;

/// <summary>
/// The broker's clients as <c>GET /operator/clients</c> answers, with the version of the view that a long poll
/// waits past. <c>GET /admin/clients</c> lists the same rows bare.
/// </summary>
public sealed record ClientList
{
	/// <summary>Every client the broker is keeping, the earliest connected first.</summary>
	public IReadOnlyList<ClientSummary> Clients { get; init; } = [];

	/// <summary>
	/// What the view held when it was read, as an opaque string: passed back as <c>after</c> with a
	/// <c>waitSeconds</c>, the next read waits until the view holds something else.
	/// </summary>
	public required string Version { get; init; }
}
