using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The Origin check a local http broker owes the MCP specification. Binding to loopback is not the
/// control it looks like: a page that rebinds a name it owns to 127.0.0.1 reaches the broker from
/// the browser, which is on loopback too, and the header is what tells a request a program made
/// from one a page made.
/// </summary>
public sealed class LoopbackOriginTests
{
	/// <summary>
	/// The load-bearing case, and the one an over-zealous check breaks: an MCP client is not a
	/// browser and sends no Origin at all. TrayRelay is one of them, so refusing an absent header
	/// would refuse every stdio session on the machine.
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Allows_a_request_that_sends_no_origin(string? origin) =>
		Assert.True(LoopbackOrigin.IsAllowed(origin));

	[Theory]
	[InlineData("http://localhost")]
	[InlineData("http://localhost:5077")]
	[InlineData("https://LOCALHOST:5077")]
	[InlineData("http://127.0.0.1:5077")]
	[InlineData("http://127.5.5.5:5077")]
	[InlineData("http://[::1]:5077")]
	public void Allows_an_origin_naming_this_machine(string origin) =>
		Assert.True(LoopbackOrigin.IsAllowed(origin));

	/// <summary>
	/// The names that read as loopback and are not. A suffix match would pass the first two and a
	/// prefix match the third, which is why the host is compared whole.
	/// </summary>
	[Theory]
	[InlineData("http://evil.com")]
	[InlineData("https://notlocalhost")]
	[InlineData("http://localhost.evil.com")]
	[InlineData("http://127.0.0.1.evil.com")]
	[InlineData("http://192.168.1.10:5077")]
	public void Refuses_an_origin_naming_anywhere_else(string origin) =>
		Assert.False(LoopbackOrigin.IsAllowed(origin));

	/// <summary>
	/// A value that cannot be read cannot be vouched for, and "unreadable" is the shape an attempt to
	/// slip past a check has. "null" is its own case: a sandboxed iframe or a file:// page sends it,
	/// and it names nowhere at all.
	/// </summary>
	[Theory]
	[InlineData("null")]
	[InlineData("localhost")]
	[InlineData("not a uri")]
	public void Refuses_an_origin_it_cannot_read(string origin) =>
		Assert.False(LoopbackOrigin.IsAllowed(origin));
}
