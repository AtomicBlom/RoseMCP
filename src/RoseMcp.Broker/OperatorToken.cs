using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace RoseMcp.Broker;

/// <summary>
/// The secret an operator surface is gated on: a bearer token minted per run of the host, handed to
/// the inspector it launches, and never given to an MCP client.
/// <para>
/// It exists because the operator endpoints deliberately skip the per-client ownership check that
/// every agent-facing tool applies. That check is not decoration -- a session id is eight hex
/// characters, and the check is what stops one client reaching another's debugger by guessing one.
/// An endpoint that must see every session, because the person running the broker owns all of them,
/// reintroduces that hole for any process running as this user unless it holds something an agent
/// does not.
/// </para>
/// <para>
/// Minted per run rather than persisted. An inspector holding yesterday's token should be told to
/// open a new one from the tray, not silently authorised: the tray is the thing that knows the
/// person asking is at the keyboard.
/// </para>
/// </summary>
public sealed class OperatorToken
{
	private const string Scheme = "Bearer ";

	private readonly byte[] _expected;

	/// <summary>Wraps a token somebody else chose, which is how <c>ROSEMCP_TOKEN</c> is honoured.</summary>
	public OperatorToken(string value)
	{
		Value = value;
		_expected = Encoding.UTF8.GetBytes(value);
	}

	/// <summary>The token itself, for putting on a command line or in a log.</summary>
	public string Value { get; }

	/// <summary>
	/// A fresh token: 32 random bytes, base64url with no padding, so it survives a command line, a
	/// URL and a header without escaping.
	/// </summary>
	public static OperatorToken Mint() => new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

	/// <summary>
	/// The token from <c>ROSEMCP_TOKEN</c> when it is set, else a fresh one. <paramref name="minted"/>
	/// says which, because a host that chose the token is the only one that has to tell anybody
	/// what it is.
	/// </summary>
	public static OperatorToken FromEnvironmentOrMint(out bool minted)
	{
		var configured = Environment.GetEnvironmentVariable("ROSEMCP_TOKEN");

		minted = string.IsNullOrWhiteSpace(configured);

		return minted ? Mint() : new OperatorToken(configured!);
	}

	/// <summary>
	/// Whether an <c>Authorization</c> header carries this token.
	/// <para>
	/// The scheme is matched case-insensitively, as the specification requires, and the value is not:
	/// it is a secret, not a word. The comparison is fixed-time over the bytes, after a length check
	/// -- lengths are not secret, and comparing different lengths is what the fixed-time helper
	/// refuses to do.
	/// </para>
	/// </summary>
	public bool Matches(string? authorization)
	{
		if (authorization is null) return false;
		if (!authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

		var supplied = Encoding.UTF8.GetBytes(authorization[Scheme.Length..]);

		return supplied.Length == _expected.Length && CryptographicOperations.FixedTimeEquals(supplied, _expected);
	}
}
