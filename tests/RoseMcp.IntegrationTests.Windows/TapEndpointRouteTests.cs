using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Xaml;
using RoseMcp.Symbols;
using RoseMcp.Tap.Endpoint;
using RoseMcp.Tap.Protocol;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// The route a dropped-in tap takes into an app, end to end inside this process: a framework's endpoint
/// listening as a framework hosts it, the host asking it to load the test tap, and the tap greeting the
/// host's pipe and answering on it. This process plays the target, which is the only way to make the
/// endpoint refuse, or be served by the wrong process, on demand.
/// </summary>
public sealed class TapEndpointRouteTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private static readonly string TestTapPath = typeof(TapEndpointRouteTests).Assembly
		.GetCustomAttributes<AssemblyMetadataAttribute>()
		.Single(attribute => attribute.Key == "TestTapPath")
		.Value!;

	/// <summary>The test tap as discovery reads it from its metadata, with an endpoint name no other test shares.</summary>
	private static DroppedInTap TestTap()
	{
		var tap = TapManifest.Read(TestTapPath).ShouldNotBeNull("the test tap carries the tap attribute");
		return tap with { Endpoint = $"rosemcp-testtap-{Guid.NewGuid():N}-{{pid}}" };
	}

	[Test]
	public void Loads_a_dropped_in_tap_through_its_endpoint_and_reads_it_over_the_host_pipe()
	{
		var tap = TestTap();
		var pid = Environment.ProcessId;
		using var endpoint = TapEndpoint.Start(EndpointWire.PipeName(tap.Endpoint, pid), "Test", "1.0");
		using var pipe = new XamlProviderPipe(NullLogger.Instance, TapProtocolRange.DroppedIn);
		pipe.Listen().ShouldBeNull();

		TapEndpointClient.Listening(endpoint.PipeName).ShouldBeTrue();

		var refusal = TapEndpointClient.Load(pid, tap, $"{Path.GetTempPath()}|{pipe.Name}|{pipe.Nonce}", Patience);

		refusal.ShouldBeNull();
		pipe.WaitForProvider(Patience).ShouldNotBeNull(pipe.Refused);
		pipe.Capabilities.Serves(TapCommands.Tree).ShouldBeTrue();
		pipe.Capabilities.Serves(TapCommands.Properties).ShouldBeFalse("the test tap declares only tree and detach");
		TapWire.Fields(pipe.Request(TapCommands.Tree, Patience).ShouldNotBeNull())[4].ShouldBe("Root");
	}

	/// <summary>
	/// A pipe named for the target but created by another process is the impostor the host checks for: it would be
	/// handed the host's pipe name and key, and could pose as the tap. Here the endpoint is in this process and the
	/// target is another, so this process is the impostor.
	/// </summary>
	[Test]
	public void Sends_nothing_to_an_endpoint_served_by_a_process_other_than_the_target()
	{
		var target = Environment.ProcessId + 4;
		var tap = TestTap();
		using var endpoint = TapEndpoint.Start(EndpointWire.PipeName(tap.Endpoint, target), "Test", "1.0");

		var refusal = TapEndpointClient.Load(target, tap, "unused|unused|unused", Patience);

		refusal.ShouldNotBeNull().ShouldContain($"process {Environment.ProcessId}");
		refusal.ShouldContain("nothing was sent");
	}

	[Test]
	public void Says_why_the_endpoint_refused_to_load_the_tap()
	{
		var tap = TestTap() with { EntryType = "RoseMcp.TestTap.Missing" };
		var pid = Environment.ProcessId;
		using var endpoint = TapEndpoint.Start(EndpointWire.PipeName(tap.Endpoint, pid), "Test", "1.0");

		var refusal = TapEndpointClient.Load(pid, tap, "unused|unused|unused", Patience);

		refusal.ShouldNotBeNull().ShouldContain("has no type RoseMcp.TestTap.Missing");
		refusal.ShouldContain("Test 1.0 endpoint");
	}

	[Test]
	public void A_pipe_nobody_created_is_not_listening()
	{
		TapEndpointClient.Listening($"rosemcp-testtap-{Guid.NewGuid():N}").ShouldBeFalse();
	}
}
