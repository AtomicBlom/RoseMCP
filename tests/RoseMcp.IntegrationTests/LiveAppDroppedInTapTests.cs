using RoseMcp.Contracts;
using RoseMcp.Settings;

using static RoseMcp.IntegrationTests.ProbeTargetSession;
using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A target no in-box tap serves, read through the whole tool path: a debugger attached by the broker's
/// live-app host, the test tap found in the folder <see cref="TapFolders.Variable"/> names, loaded through
/// the endpoint the target hosts, and read over the same pipe the in-box taps answer on.
/// <para>
/// The test tap declares only <c>tree</c> and <c>detach</c>, so the same session also shows the host
/// refusing a command the tap did not declare, naming the tap, without sending it.
/// </para>
/// </summary>
public sealed class LiveAppDroppedInTapTests
{
	/// <summary>
	/// The live-app host is a child of this process and inherits its environment, so the folder is named here.
	/// Added to whatever is already named rather than replacing it, because only taps are looked for there:
	/// the in-box taps are chosen first, so no other test's target is served differently for it.
	/// </summary>
	private static void NameTheTestTapFolder()
	{
		var folder = Path.Combine(RepositoryRoot(), "tests", "RoseMcp.TestTap", "bin", Configuration(), "net10.0");
		File.Exists(Path.Combine(folder, "RoseMcp.TestTap.dll")).ShouldBeTrue($"The test tap was not built into {folder}.");

		var named = Environment.GetEnvironmentVariable(TapFolders.Variable);
		if (named?.Split(Path.PathSeparator).Contains(folder, StringComparer.OrdinalIgnoreCase) == true) return;

		Environment.SetEnvironmentVariable(TapFolders.Variable, string.IsNullOrEmpty(named) ? folder : named + Path.PathSeparator + folder);
	}

	private static string TapProbeTargetPath()
	{
		var exe = Path.Combine(RepositoryRoot(), "tests", "TapProbeTarget", "bin", Configuration(), "net10.0", "TapProbeTarget.exe");
		if (!File.Exists(exe)) throw new FileNotFoundException("The tap probe target was not built.", exe);

		return exe;
	}

	[Test]
	public async Task Reads_the_tree_of_a_target_a_dropped_in_tap_serves_and_refuses_what_it_does_not_declare()
	{
		NameTheTestTapFolder();

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var child = StartProcess(TapProbeTargetPath());
		try
		{
			var session = await manager.StartAsync(AttachTo(child.Id), cancellationToken);
			session.Describe().State.ShouldBe(LiveAppSessionState.Ready);

			// The endpoint comes up as the target starts, which can be a moment after the debugger attached.
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			while (tree.Nodes is not { Count: > 0 } && DateTime.UtcNow < deadline)
			{
				await Task.Delay(500, cancellationToken);
				tree = await session.ReadXamlTreeAsync(cancellationToken);
			}

			var root = (tree.Nodes ?? []).ShouldHaveSingleItem($"the test tap serves one element; detail: {tree.Detail}");
			root.TypeName.ShouldBe("TestTap.Root");
			root.Address.ShouldBe("#Root");

			var properties = await session.ReadXamlPropertiesAsync(root.Handle, includeDefaults: false, cancellationToken);
			properties.Detail.ShouldNotBeNull().ShouldContain("The TestTap tap serving this app does not serve 'properties'");

			var described = session.Describe();
			described.XamlStack.ShouldBe(XamlStack.Tap);
			described.XamlTap.ShouldBe("TestTap");

			(await manager.CloseAsync(session.SessionId, cancellationToken)).ShouldBeTrue();
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}
}
