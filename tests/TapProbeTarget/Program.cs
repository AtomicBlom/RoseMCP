using RoseMcp.Tap.Endpoint;
using RoseMcp.Tap.Protocol;

namespace TapProbeTarget;

/// <summary>
/// A target for the dropped-in tap tests: it listens on the endpoint the test tap names,
/// <c>rosemcp-testtap-{pid}</c>, exactly as a framework's endpoint listens in an app built on it, and
/// otherwise idles. RoseMCP attaches a debugger, finds the test tap in the folder the test names, and
/// loads it through this endpoint.
/// <para>
/// It ends itself after ten minutes, the bound the debug probe uses, so an orphan left by a crashed
/// test cannot outlive the run.
/// </para>
/// </summary>
internal static class Program
{
	private static void Main()
	{
		using var endpoint = TapEndpoint.Start(
			EndpointWire.PipeName("rosemcp-testtap-{pid}", Environment.ProcessId),
			"TapProbe",
			"1.0",
			line => Console.Error.WriteLine(line));

		Console.WriteLine($"listening on {endpoint.PipeName}");

		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
		while (DateTime.UtcNow < deadline) Thread.Sleep(200);
	}
}
