using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The live-app tools' answers where reaching them takes no debugged process: a session id that
/// names nothing open.
/// </summary>
public sealed class LiveAppDebugToolsTests
{
	/// <summary>
	/// Detaching a session that is not open is an answer, not a failure, and it names the session it
	/// is about -- the one the caller passed, since there is no session to read a name from. A caller
	/// detaching several in turn can tell each answer from the next by its fields rather than by
	/// matching a sentence.
	/// </summary>
	[Test]
	public async Task Detaching_a_session_that_is_not_open_names_it_and_says_nothing_was_detached()
	{
		await using var sessions = new LiveAppSessionManager(
			Options.Create(new BrokerOptions()),
			NullLoggerFactory.Instance,
			NullLogger<LiveAppSessionManager>.Instance);

		var tools = new LiveAppDebugTools(
			sessions,
			NoInspector.WithoutAnEndpoint,
			new CallerPaths(Options.Create(new BrokerOptions())));

		var detached = await tools.DetachAsync("no-such-session");

		detached.ShouldBe(new LiveSessionDetached { SessionId = "no-such-session", Detached = false });
	}
}
