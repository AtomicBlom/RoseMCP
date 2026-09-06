using System.Net;

namespace RoseMcp.Broker;

/// <summary>
/// Whether a request's <c>Origin</c> header is one a local http broker may answer.
/// <para>
/// The MCP specification requires a local http server to validate <c>Origin</c>, and the attack it
/// names is DNS rebinding: a page the user is merely looking at resolves a name it controls to
/// 127.0.0.1 and the browser then sends requests to this broker from that page, with the user's own
/// network position. Binding to loopback does not stop it, because the browser is on loopback too.
/// The header is what separates a request a program made from one a page made, since a browser sets
/// it and will not let script forge it.
/// </para>
/// <para>
/// Absent is allowed, and that is the whole design rather than a gap: an MCP client is not a browser
/// and sends no Origin at all, so refusing its absence would refuse every real caller -- including
/// TrayRelay, which sends none. What is refused is an Origin that is present and names somewhere
/// other than this machine, which no legitimate caller here produces.
/// </para>
/// </summary>
public static class LoopbackOrigin
{
	/// <summary>The hosts an Origin may name, matched whole rather than as a suffix.</summary>
	private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"localhost", "127.0.0.1", "[::1]", "::1",
	};

	/// <summary>
	/// True when the request may be served: no <c>Origin</c>, or one naming this machine.
	/// <para>
	/// Anything that does not parse as an absolute URI is refused rather than passed through. A value
	/// this cannot read is a value it cannot vouch for, and "unreadable" is exactly the shape an
	/// attempt to slip past a check has.
	/// </para>
	/// </summary>
	public static bool IsAllowed(string? origin)
	{
		if (string.IsNullOrWhiteSpace(origin)) return true;

		// Its own case: a sandboxed iframe or a file:// page sends this, and it names nowhere at all.
		if (string.Equals(origin, "null", StringComparison.Ordinal)) return false;

		if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

		return Hosts.Contains(uri.Host) || IPAddress.IsLoopback(Address(uri));
	}

	/// <summary>
	/// The host as an address, or none when it is a name. A name other than the ones above is not
	/// resolved: what it resolves to now is not what it will resolve to when the request arrives, and
	/// trusting a lookup is the rebinding attack itself.
	/// </summary>
	private static IPAddress Address(Uri uri) =>
		IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) ? address : IPAddress.None;
}
