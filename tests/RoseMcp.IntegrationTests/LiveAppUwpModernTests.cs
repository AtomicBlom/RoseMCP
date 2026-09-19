using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The live-app session against a UWP app on modern .NET: <c>UseUwp</c> on net10.0-windows, which is
/// Windows.UI.Xaml in an AppContainer behind an AUMID, over CoreCLR through a CsWinRT projection.
/// <para>
/// Two tests, and what they establish is that the UWP tap serves it unmodified -- the same endpoint,
/// the same initialiser and the same dispatcher seam. That had been claimed of this shape without any
/// such process ever having been run, which is the gap these close; see <see cref="UwpModernProbeApp"/>
/// for what differs underneath and why it is a fixture of its own.
/// </para>
/// </summary>
[Category("ProbeApp")]
[ClassDataSource<UwpModernProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppUwpModernTests(UwpModernProbeApp uwpModern)
{
	/// <summary>
	/// A UWP app on modern .NET, launched from birth and debugged (#117).
	/// </summary>
	/// <remarks>
	/// The runtime half, on its own, because it is the half that actually differs. The app model and
	/// the XAML framework are the classic probe's; what is new underneath is CoreCLR reached through a
	/// CsWinRT projection, which puts every managed frame behind an ABI layer -- the startup exception
	/// arrives through <c>ABI.Windows.UI.Xaml.IApplicationOverrides.Do_Abi_OnLaunched_1</c> rather
	/// than a direct framework call. Nothing here had seen that shape before, so "the debugger still
	/// resolves our types through it" is worth asserting rather than assuming.
	/// <para>
	/// It also pins the thing the classic probe cannot: uwp-classic is debuggable only as Debug x64,
	/// because every other configuration forces .NET Native. This one is CoreCLR in every architecture
	/// it builds, and the fixture builds it for the host's.
	/// </para>
	/// </remarks>
	[Test]
	[ModernUwpProbe]
	public async Task Launches_and_debugs_the_modern_uwp_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var turn = await uwpModern.TakeAsync(needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "modern uwp probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		// Thrown once in OnLaunched, before the window is shown, so only a debugger that was present
		// from the runtime's first breath sees it. An attach landing a beat after activation is already
		// too late, which is what makes this the from-birth assertion rather than merely a liveness one.
		var startup = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernStartupException") ?? false),
			cancellationToken);
		Assert.NotNull(startup);

		var ticking = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(ticking);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The live XAML tree and one element's properties, on a UWP app running modern .NET (#117).
	/// </summary>
	/// <remarks>
	/// The claim under test is that the UWP tap serves this app unmodified -- same endpoint, same
	/// initialiser, same dispatcher seam, same AppContainer grants -- because the XAML framework is
	/// the same Windows.UI.Xaml regardless of which runtime is calling into it. That is a claim about
	/// something the tap cannot see, so it is the kind that holds right up until it does not.
	/// <para>
	/// Properties are read in the same test rather than a second one, deliberately. They need the app
	/// up, and bringing a packaged app up is six seconds; splitting them buys isolation this pair does
	/// not need, since reading a tree and reading a property of it are the same operation twice.
	/// </para>
	/// </remarks>
	[Test]
	[ModernUwpProbe]
	public async Task Reads_the_xaml_tree_of_a_modern_uwp_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var turn = await uwpModern.TakeAsync(needsXamlProvider: true, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "modern uwp xaml probe",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		// Well into running, so an empty tree cannot be an app that has not built one yet.
		var running = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseUwpModernProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(running);

		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
		Assert.NotEmpty(tree.Nodes);

		// The same names the classic UWP probe declares, because the two apps mirror each other on
		// purpose -- the modern one is generated from the classic one's markup.
		foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
		{
			Assert.Contains(tree.Nodes, node => node.Name == name);
		}

		var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Panel");
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Caption");
		Assert.DoesNotContain(panelSubtree.Nodes, node => node.Name == "RootGrid");

		// Source info survives the projection. It comes from the markup compiler rather than the
		// runtime, and a UseUwp project runs the same compiler, so it should -- but it is the one piece
		// of this that is generated at build time rather than read off the live tree.
		var pane = Assert.Single(tree.Nodes, node => node.Name == "Pane");
		var properties = await session.ReadXamlPropertiesAsync(pane.Handle, includeDefaults: false, cancellationToken);
		Assert.NotEmpty(properties.Properties);
		Assert.Contains(properties.Properties, property => property.Name == "CornerRadius");

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}
}
