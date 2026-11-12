using RoseMcp.Tap.Endpoint;
using RoseMcp.Tap.Protocol;

namespace TapProbeTarget;

/// <summary>
/// A target for the dropped-in tap tests: it listens on the endpoint the test tap names,
/// <c>rosemcp-testtap-{pid}</c>, exactly as a framework's endpoint listens in an app built on it, and
/// otherwise idles. RoseMCP attaches a debugger, finds the test tap in the folder the test names, and
/// loads it through this endpoint.
/// <para>
/// The endpoint loads taps only from the folders <c>ROSEMCP_TAPS</c> names, which the test sets before
/// starting this process, so the route is proved with the folder restriction a careful framework turns
/// on rather than with an endpoint that loads from anywhere. Where the variable is unset it loads from
/// any path, as an endpoint started without folders does.
/// </para>
/// <para>
/// It ends itself after ten minutes, the bound the debug probe uses, so an orphan left by a crashed
/// test cannot outlive the run.
/// </para>
/// </summary>
internal static class Program
{
	/// <summary>The variable RoseMCP reads its extra tap folders from, separated as a path list is.</summary>
	private const string TapFoldersVariable = "ROSEMCP_TAPS";

	private static void Main()
	{
		var tapFolders = Environment.GetEnvironmentVariable(TapFoldersVariable)?
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		using var endpoint = TapEndpoint.Start(
			EndpointWire.PipeName("rosemcp-testtap-{pid}", Environment.ProcessId),
			"TapProbe",
			"1.0",
			line => Console.Error.WriteLine(line),
			tapFolders is { Length: > 0 } ? tapFolders : null);

		Console.WriteLine($"listening on {endpoint.PipeName}");

		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
		while (DateTime.UtcNow < deadline) Thread.Sleep(200);
	}
}
