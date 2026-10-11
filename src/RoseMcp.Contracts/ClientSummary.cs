namespace RoseMcp.Contracts;

/// <summary>
/// One client of the broker, as <c>GET /admin/clients</c> and <c>GET /operator/clients</c> list it: an agent
/// session behind a relay, named by what its relay said of it, or a client that reached the broker over
/// http without one, which nothing names.
/// </summary>
public sealed record ClientSummary
{
	/// <summary>
	/// An id the broker minted for this row, the same for as long as the row lasts. It grants nothing: it is
	/// not the name a relay sends with its calls, which no answer carries.
	/// </summary>
	public required string Id { get; init; }

	/// <summary>
	/// Whether a relay said whom this client is. False for a client that connected over http without one,
	/// which the broker can tell apart only by its transport session where it has one, and not at all where it
	/// has none: those share one unnamed row.
	/// </summary>
	public required bool Named { get; init; }

	/// <summary>When this broker first heard from the client.</summary>
	public required DateTimeOffset ConnectedUtc { get; init; }

	/// <summary>When the client last called a tool here, or null where it has not.</summary>
	public DateTimeOffset? LastCallUtc { get; init; }

	/// <summary>How many tool calls the client has made here.</summary>
	public int Calls { get; init; }

	/// <summary>
	/// For a named client, whether its relay holds <see cref="RelayProtocol.WatchRoute"/> open now, which is
	/// what connected means for a relay; null for an unnamed client, which nothing tells the broker has gone.
	/// </summary>
	public bool? Watching { get; init; }

	/// <summary>The relay's own process, where it said.</summary>
	public int? RelayProcessId { get; init; }

	/// <summary>
	/// Whether the client holds the tools this broker offers: for a named client as its relay last said; for
	/// an unnamed one, whether it has listed them from this broker, since one that calls without listing holds
	/// a list some broker before this one gave it. Null where nothing says.
	/// </summary>
	public bool? ToolsCurrent { get; init; }

	/// <summary>A hash of what a named client holds, which tells two sessions holding the same tools apart from two that differ.</summary>
	public string? HeldTools { get; init; }

	/// <summary>See <see cref="RelayClient.WorkingDirectory"/>.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>See <see cref="RelayClient.Roots"/>.</summary>
	public IReadOnlyList<string> Roots { get; init; } = [];

	/// <summary>See <see cref="RelayClient.Branch"/>.</summary>
	public string? Branch { get; init; }

	/// <summary>See <see cref="RelayClient.ClientProcessId"/>.</summary>
	public int? ClientProcessId { get; init; }

	/// <summary>See <see cref="RelayClient.ClientProcessName"/>.</summary>
	public string? ClientProcessName { get; init; }

	/// <summary>The client's own name from its <c>clientInfo</c>: as its relay said, or as an unnamed client gave it the transport.</summary>
	public string? ClientName { get; init; }

	/// <summary>The client's version from its <c>clientInfo</c>.</summary>
	public string? ClientVersion { get; init; }

	/// <summary>See <see cref="RelayClient.Label"/>.</summary>
	public string? Label { get; init; }
}
