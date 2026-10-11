namespace RoseMcp.Contracts;

/// <summary>
/// What the stdio relay and the broker it forwards to agree on beyond MCP itself: the headers a
/// relay identifies itself with, the routes it reads the broker's instance from and holds open to
/// notice the broker going, and the folder an install keeps the relay in.
/// <para>
/// It has a version of its own because the relay is installed apart from everything else and a
/// deploy leaves it running. A relay forwards MCP, and MCP between two builds of Rose is the same
/// protocol however much the tools behind it change, so a relay from an older deploy serves a newer
/// broker perfectly well -- until this contract changes. Then the deploy, and only then, replaces
/// the relay and stops the copies running, and their sessions reconnect. See
/// <c>docs/decisions/a-deploy-replaces-the-relay-only-when-its-protocol-changes.md</c>.
/// </para>
/// </summary>
public static class RelayProtocol
{
	/// <summary>
	/// The version of this contract. Raise it with any change a relay of the previous version would
	/// misread: a header, a route or the shape it answers, a <c>_meta</c> key the relay sends, or
	/// what an instance id means. Raise it too with any change to what the relay itself does, such as a
	/// route it starts calling: a deploy replaces an installed relay only when this number moves, so a
	/// relay change shipped without it never reaches a running install. <c>tools/published-layout.json</c>
	/// carries the same number, which is what a deploy compares with the installed relay's, and a unit
	/// test holds the two together.
	/// </summary>
	public const int Version = 2;

	/// <summary>
	/// The folder under an install root the relay is installed in, apart from the files a deploy
	/// replaces. The published layout names the same folder.
	/// </summary>
	public const string Folder = "relay";

	/// <summary>The request header a relay names itself in: an id it mints when it starts and keeps across reconnects.</summary>
	public const string RelayHeader = "Rose-Relay";

	/// <summary>The request header a relay sends its <see cref="Version"/> in.</summary>
	public const string VersionHeader = "Rose-Relay-Protocol";

	/// <summary>
	/// The request header a relay sends its process id in, so a deploy can tell the relays of the
	/// install it replaced from every other relay watching the same port: one run from a build output
	/// or another install is counted by the broker all the same.
	/// </summary>
	public const string ProcessHeader = "Rose-Relay-Process";

	/// <summary>
	/// Where a broker says who it is to a relay: its instance, its build, the relay protocol it
	/// speaks, and how many relays are watching it and from which processes. Answers a
	/// <see cref="RelayHello"/>.
	/// </summary>
	public const string HelloRoute = "/admin/relays";

	/// <summary>
	/// The request a relay holds open for as long as the broker lives. The broker writes one line,
	/// the <see cref="RelayHello"/>, and then nothing until it stops, so the response ending is how a
	/// relay learns at once that the broker has gone -- a killed process closes its sockets -- without
	/// asking it anything on a timer. While it is open the relay is counted as connected.
	/// </summary>
	public const string WatchRoute = "/admin/relays/watch";

	/// <summary>
	/// Where a relay says whom it serves: a POST of a <see cref="RelayClient"/>, with the relay's id in
	/// <see cref="RelayHeader"/>, answered with no content. Sent once the client has introduced itself, and
	/// again after every reconnect and whenever what it says changes, since the broker keeps it only in memory.
	/// </summary>
	public const string ClientRoute = "/admin/relays/client";

	/// <summary>A new relay id: thirty-two lowercase hex digits.</summary>
	public static string NewRelayId() => Guid.NewGuid().ToString("N");

	/// <summary>
	/// Whether <paramref name="value"/> is in the shape <see cref="NewRelayId"/> mints. The broker
	/// keeps the id while the relay is connected, so anything else is refused rather than stored.
	/// </summary>
	public static bool IsRelayId(string? value) =>
		value is { Length: 32 } && value.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));
}
