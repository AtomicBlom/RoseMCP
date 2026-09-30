using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The secret the operator API is gated on.
/// <para>
/// It carries more weight than an ordinary check, because the endpoints behind it deliberately skip
/// the per-client ownership test every agent-facing debug tool applies. That test is what stops one
/// MCP client reaching another's debugger by guessing an eight-character session id; this token is
/// the only thing standing in its place.
/// </para>
/// </summary>
public sealed class OperatorTokenTests
{
	[Test]
	public void Mints_a_token_that_survives_a_command_line_and_a_header()
	{
		var token = OperatorToken.Mint().Value;

		// 32 bytes as base64url with no padding. Nothing in that alphabet needs escaping in a shell,
		// a URL or a header, which is the point of choosing it.
		token.Length.ShouldBe(43);
		token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ShouldBeTrue(
			"a minted token is base64url, so nothing needs escaping anywhere it is passed");
	}

	[Test]
	public void Mints_a_different_token_every_time()
	{
		var minted = Enumerable.Range(0, 20).Select(_ => OperatorToken.Mint().Value).ToHashSet();

		minted.Count.ShouldBe(20);
	}

	[Test]
	public void Accepts_its_own_token()
	{
		var token = OperatorToken.Mint();

		token.Matches($"Bearer {token.Value}").ShouldBeTrue();
	}

	/// <summary>
	/// The scheme is a word and the token is not. The specification asks for the scheme to be
	/// matched without regard to case; doing the same to the value would throw away most of a
	/// secret's strength.
	/// </summary>
	[Test]
	[Arguments("bearer")]
	[Arguments("BEARER")]
	[Arguments("BeArEr")]
	public void Matches_the_scheme_whatever_its_case(string scheme)
	{
		var token = OperatorToken.Mint();

		token.Matches($"{scheme} {token.Value}").ShouldBeTrue();
	}

	[Test]
	public void Refuses_a_token_whose_case_differs()
	{
		var token = new OperatorToken("AbCdEf");

		token.Matches("Bearer abcdef").ShouldBeFalse("the value is a secret, not a word");
	}

	/// <summary>
	/// Each of these has reached the check in practice: a client with no token at all, one sending a
	/// different scheme, one sending the value bare, and one sending a value of the wrong length --
	/// which is the case the fixed-time comparison itself refuses to handle, so the length is checked
	/// before it.
	/// </summary>
	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments("Bearer")]
	[Arguments("Bearer ")]
	[Arguments("Basic abcdef")]
	[Arguments("abcdef")]
	[Arguments("Bearer abcde")]
	[Arguments("Bearer abcdefg")]
	public void Refuses_anything_else(string? authorization)
	{
		var token = new OperatorToken("abcdef");

		token.Matches(authorization).ShouldBeFalse($"'{authorization}' is not this token");
	}

	/// <summary>
	/// A token somebody set is honoured as given, which is what lets ROSEMCP_TOKEN gate a server
	/// whose operator surface has to agree with its MCP one.
	/// </summary>
	[Test]
	public void Keeps_a_token_it_was_given()
	{
		var token = new OperatorToken("a-token-somebody-chose");

		token.Value.ShouldBe("a-token-somebody-chose");
		token.Matches("Bearer a-token-somebody-chose").ShouldBeTrue();
	}
}
