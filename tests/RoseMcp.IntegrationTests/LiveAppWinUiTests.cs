using System.Diagnostics;
using RoseMcp.Contracts;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The live-app session against the WinUI 3 probe app, unpackaged and packaged: the reads and edits
/// the UWP suite makes, against the other XAML framework. Which provider serves a target is decided
/// by the framework that target runs, and one built for either cannot serve the other, so this is the
/// half of the XAML surface no UWP test can reach.
/// <para>
/// Leased from <see cref="WinUiProbeApp"/>, which is a separate fixture from the UWP one on purpose
/// and says why: a gate exists to stop two tests driving one single-instance app, and a WinUI test
/// drives a different process sharing no package, provider or window with the UWP probe.
/// </para>
/// </summary>
[Category("ProbeApp")]
[ClassDataSource<WinUiProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppWinUiTests(WinUiProbeApp winui)
{
	/// <summary>
	/// The WinUI 3 path, unpackaged (#106). An unpackaged WinUI 3 app is an ordinary desktop process
	/// with no package identity and no AppContainer, which is the shape the live-app half kept getting
	/// wrong, so it is the one worth proving first.
	/// <para>
	/// The debugger needs no WinUI-specific code (#79) -- this is here to keep that true rather than
	/// to establish it, and to give the seams work (#75) something to run against.
	/// </para>
	/// </summary>
	[Test]
	[WinUiProbe]
	public async Task Launches_and_debugs_the_unpackaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = turn.ExecutablePath,
			Description = "winui probe (unpackaged)",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		var marker = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(marker);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The same app, packaged. Worth its own test because packaged and unpackaged are different
	/// targets rather than two ways of shipping one: this one has package identity and is activated
	/// by AUMID rather than launched by path.
	/// <para>
	/// It is still not in an AppContainer, which is the thing measuring this settled. A packaged WinUI
	/// 3 app is a packaged *desktop* app -- runFullTrust, Windows.FullTrustApplication -- so packaging
	/// and sandboxing come apart here in a way they never do for classic UWP, and only the UWP tap
	/// needs the work folder granted to ALL APPLICATION PACKAGES.
	/// </para>
	/// </summary>
	[Test]
	[WinUiProbe]
	public async Task Launches_and_debugs_the_packaged_winui_probe_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: true, needsXamlProvider: false, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = turn.Aumid,
			Description = "winui probe (packaged)",
		};

		var session = await manager.StartAsync(target, cancellationToken);
		var summary = session.Describe();
		Assert.True(
			summary.State == LiveAppSessionState.Ready,
			$"expected Ready, got {summary.State}: {summary.Detail} (arch {summary.Architecture})");

		var marker = await WaitForEventAsync(
			session,
			entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(marker);

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The XAML tree of a WinUI 3 target this session started (#76).
	/// </summary>
	/// <remarks>
	/// <para>
	/// This was a refusal test until the cause was found, and its inversion is the signal that #76 is
	/// done -- which is what the refusal's own comment said would happen.
	/// </para>
	/// <para>
	/// What it proves past "a tree came back" is that the shared tap serves a second framework
	/// unchanged: the same walk, the same snapshot and the same named elements as the UWP probe, out
	/// of Microsoft.UI.Xaml. The one thing WinUI 3 needed was that the walk not be advised from the
	/// UI thread, and that lives in the provider seam rather than here.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_launched()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		await using var manager = CreateManager();

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = turn.ExecutablePath,
			Description = "winui xaml probe",
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
				&& (entry.ExceptionType?.Contains("RoseWinUiProbeException") ?? false),
			cancellationToken);
		Assert.NotNull(running);

		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
		Assert.NotEmpty(tree.Nodes);

		// The same names the UWP probe declares, because the two apps mirror each other on purpose.
		foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
		{
			Assert.Contains(tree.Nodes, node => node.Name == name);
		}

		// Rooting works the same here: a named element's subtree carries its descendants and not its
		// parent. Asserted on WinUI too because the address grammar is computed from the live tree,
		// and the live tree is the half that differs between the frameworks.
		var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Panel");
		Assert.Contains(panelSubtree.Nodes, node => node.Name == "Caption");
		Assert.DoesNotContain(panelSubtree.Nodes, node => node.Name == "RootGrid");

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
	}

	/// <summary>
	/// The XAML tree of a WinUI 3 app this session attached to rather than started (#76).
	/// </summary>
	/// <remarks>
	/// The companion to the launched case, and the one that settles the premise both refusals rested
	/// on. WinUI 3 was believed to need diagnostics enabled from startup, so that attaching could
	/// never work; it does work, because that belief was inferred from a failure whose real cause was
	/// a deadlock of our own making. Attaching is also the case an agent actually meets -- the app is
	/// already running by the time anyone asks about it -- so it is worth its own test rather than
	/// being assumed to follow from the launched one.
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_of_a_winui_app_it_attached_to()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		// Started outside the session on purpose: nothing about this process was arranged for us.
		using var child = StartProcess(turn.ExecutablePath);
		await using var manager = CreateManager();

		try
		{
			// Waits for the window rather than for a fixed six seconds, so a probe that died at startup
			// says so instead of being attached to (#129).
			await WaitForProbeWindowAsync(child, cancellationToken);

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "winui probe (attached)",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			Assert.Equal(LiveAppSessionState.Ready, session.Describe().State);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
			Assert.NotEmpty(tree.Nodes);

			foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
			{
				Assert.Contains(tree.Nodes, node => node.Name == name);
			}

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A second session over an app a first session has already inspected and let go of.
	/// </summary>
	/// <remarks>
	/// The scenario an agent meets whenever it comes back to an app it looked at earlier, and nothing
	/// covered it. The provider cannot be unloaded, so the second attach meets a target that already
	/// has one loaded, with the first session's channel torn down under it.
	/// <para>
	/// What this does <em>not</em> prove is that the provider's pipe reader can be restarted, which is
	/// the repair the C++ side of this change makes. Each host shadow-copies its own provider, so the
	/// second session loads a separate module with its own globals and never reaches the path where a
	/// reader has already stopped; the test passes with that repair and without it, which was
	/// established by reverting it and running this again rather than assumed. Reaching it would mean
	/// dropping the pipe under a live session, and the pipe belongs to the host process.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_the_xaml_tree_again_after_the_first_session_closed_the_pipe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.AttachProcess,
				ProcessId = child.Id,
				Description = "winui probe (attached twice)",
			};

			// Each manager owns its own pipe, so disposing the first is what takes the channel away
			// under a provider that goes on running.
			var firstLogs = new RecordingLoggerFactory();

			await using (var first = CreateManager(firstLogs))
			{
				var session = await first.StartAsync(target, cancellationToken);
				var tree = await session.ReadXamlTreeAsync(cancellationToken);

				Assert.True(tree.Detail is null, $"expected a tree, got detail: {tree.Detail}");
				Assert.Contains(tree.Nodes, node => node.Name == "RootGrid");
				Assert.Contains(firstLogs.Lines, line => line.Contains("provider connected on", StringComparison.Ordinal));
				Assert.True(await first.CloseAsync(session.SessionId, cancellationToken));
			}

			var secondLogs = new RecordingLoggerFactory();

			await using var second = CreateManager(secondLogs);

			var again = await second.StartAsync(target, cancellationToken);
			var reread = await again.ReadXamlTreeAsync(cancellationToken);

			Assert.True(reread.Detail is null, $"expected a tree on the second session, got detail: {reread.Detail}");
			Assert.Contains(reread.Nodes, node => node.Name == "RootGrid");

			// The provider connects for the second session as well, rather than the session falling
			// back to the work folder without saying so.
			Assert.Contains(secondLogs.Lines, line => line.Contains("provider connected on", StringComparison.Ordinal));

			Assert.True(await second.CloseAsync(again.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Every wait on the provider channel is bounded, and the sentence that comes back names which
	/// channel ran out and how long it was given.
	/// </summary>
	/// <remarks>
	/// The bound this drives is the injection call itself, which had none. It is a blocking
	/// cross-process call served by the target's UI thread, so a target wedged below managed code
	/// never returns from it -- which is how a full suite run hung for fifty minutes on a first tree
	/// read, the pipe logged as listening and no line after it.
	/// <para>
	/// Driven by shortening the bound rather than by wedging an app, because a wedged UI thread is not
	/// something a test can arrange on demand and a test that waits for a real hang is the very thing
	/// this is fixing. A millisecond is far below what loading a DLL into another process and walking
	/// its tree can take, so the bound expires every time; the abandoned injection completes into the
	/// work folder afterwards, harmlessly, which is why this takes the app for itself.
	/// <para>
	/// What it does not prove is that the abandoned call was genuinely blocked rather than merely
	/// slow. Nothing here can prove that: the wait is bounded either way, and the difference is
	/// invisible from this side by construction.
	/// </para>
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Bounds_the_wait_on_the_xaml_injection_call_and_names_the_channel()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			// Process-wide, and safe because the host reads it at startup and this turn holds the only
			// gate under which a live-app host is started. Zero rather than a small number: a bound of one
			// millisecond is really a wait of fifteen, because that is the scheduler's granularity, and an
			// injection into a warm app finishes inside that often enough to pass at random.
			using var shortened = new EnvironmentVariable("ROSEMCP_XAML_TIMEOUT_SECONDS", "0");

			await using var manager = CreateManager();

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (bounded injection)",
				},
				cancellationToken);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.NotNull(tree.Detail);
			Assert.Contains("the XAML diagnostics injection call", tree.Detail, StringComparison.Ordinal);
			Assert.Contains("timed out after", tree.Detail, StringComparison.Ordinal);
			Assert.Empty(tree.Nodes);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// The provider pipe serves the reads it says it does: the first read of a session injects and
	/// answers through the work folder, and every read after it is a message to the resident reader.
	/// </summary>
	/// <remarks>
	/// The pipe connected, greeted, and then served nothing, unchanged for two releases -- invisible
	/// because both channels return the same tree, so every test that asserted the tree passed
	/// either way. The result names its channel now, which is the only thing that makes this
	/// assertable at all.
	/// <para>
	/// The first read cannot use the pipe and that is by construction rather than a shortcoming: the
	/// provider is not in the app until something injects it, and the pipe name travels in that
	/// injection's initialisation data. So the first read is asserted as the work folder, which also
	/// keeps the second assertion honest -- a session that reported "pipe" for both would mean the
	/// field was not being read off the path actually taken.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task The_second_xaml_read_of_a_session_is_served_over_the_pipe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			await using var manager = CreateManager();

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (channel)",
				},
				cancellationToken);

			// The first read injects, because that is what loads the provider, and is then answered on the
			// pipe like every other request. Injection carries no request of its own, so a read that came
			// back from the work folder would mean the provider had not connected.
			var first = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.True(first.Detail is null, $"expected a tree, got detail: {first.Detail}");
			Assert.Equal("pipe", first.Channel);

			var second = await session.ReadXamlTreeAsync(cancellationToken);

			Assert.True(second.Detail is null, $"expected a tree, got detail: {second.Detail}");
			Assert.Equal("pipe", second.Channel);

			// The same tree either way, which is what made the pipe's silence invisible.
			Assert.Contains(second.Nodes, node => node.Name == "RootGrid");
			Assert.Equal(first.Count, second.Count);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Reading and editing properties on WinUI 3 (#115), which nothing covered.
	/// </summary>
	/// <remarks>
	/// The WinUI tests asserted the tree and stopped there, so every property path was exercised on
	/// UWP only -- and two places in the shared header spelled <c>Windows.UI.Xaml</c> as a literal.
	/// A CornerRadius is the one that shows: XAML diagnostics renders the struct as an empty string
	/// on both frameworks, and the rescue that reads it off the element compared the declared type
	/// against the UWP name, so on WinUI 3 it never fired and the property read back empty. Empty is
	/// indistinguishable from unset, which is why this went unnoticed: the answer looked like a
	/// framework quirk rather than a wrong comparison.
	/// <para>
	/// The apply half is here for the same reason. It is the seam the toolbar that never drew lived
	/// behind: everything inspectable said yes while the screen said no, because no WinUI provider
	/// was ever exercised past the tree.
	/// </para>
	/// </remarks>
	[Test]
	[WinUiProbe]
	public async Task Reads_and_edits_properties_on_a_winui_app()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var turn = await winui.TakeAsync(packaged: false, needsXamlProvider: true, cancellationToken);

		using var child = StartProcess(turn.ExecutablePath);
		await using var manager = CreateManager();

		try
		{
			await WaitForProbeWindowAsync(child, cancellationToken);

			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.AttachProcess,
					ProcessId = child.Id,
					Description = "winui probe (properties)",
				},
				cancellationToken);

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
			var caption = tree.Nodes.FirstOrDefault(node => node.Name == "Caption");

			Assert.NotNull(pane);
			Assert.NotNull(caption);

			var properties = await session.ReadXamlPropertiesAsync(pane!.Handle, includeDefaults: false, cancellationToken);

			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");

			// The markup sets CornerRadius="8" on Pane. The framework stringifies it as nothing, so a
			// value here is the rescue firing -- and the rescue only fires if it recognises the type
			// under its Microsoft.UI.Xaml name.
			var cornerRadius = properties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");

			Assert.NotNull(cornerRadius);
			Assert.False(
				string.IsNullOrEmpty(cornerRadius!.Value),
				"CornerRadius came back empty, which is the shared header comparing against the UWP type name.");

			// A property the framework does stringify, to show the empty one above is not simply how
			// this element reads.
			var padding = properties.Properties.FirstOrDefault(property => property.Name == "Padding");

			Assert.NotNull(padding);
			Assert.False(string.IsNullOrEmpty(padding!.Value), "Padding reports a value, which is what makes the empty one above a finding");

			// And the apply half: a property edit lands and reads back.
			var markup = Path.Combine(TestToolchain.RepositoryRoot(), "tests", "apps", "winui", "MainWindow.xaml");
			var before = await File.ReadAllTextAsync(markup, cancellationToken);
			var after = before.Replace("Text=\"Rose WinUI Probe\"", "Text=\"edited on winui\"", StringComparison.Ordinal);

			Assert.NotEqual(before, after);

			var edit = await session.ApplyXamlAsync(before, after, filePath: null, cancellationToken);

			Assert.True(edit.Detail is null, $"expected the edit to apply, got detail: {edit.Detail}");
			Assert.Equal(1, edit.Applied);
			Assert.All(edit.Results, result => Assert.Equal("applied", result.Status));

			var afterwards = await session.ReadXamlPropertiesAsync(caption!.Handle, includeDefaults: false, cancellationToken);
			var text = afterwards.Properties.FirstOrDefault(property => property.Name == "Text");

			Assert.NotNull(text);
			Assert.Equal("edited on winui", text!.Value);

			Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		}
		finally
		{
			if (!child.HasExited) child.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// Waits for the probe app to open a window, giving up as soon as the process is gone rather than
	/// on a timer.
	/// <para>
	/// It used to be a flat six-second sleep, which cost six seconds on a machine that was ready in
	/// one, and on a machine where the app died at startup it waited the same six and then attached to
	/// nothing -- so a WinUI probe that failed to bootstrap the Windows App Runtime under load
	/// presented as a test hanging or failing on an attach, with the actual cause two layers down and
	/// no message anywhere (#129).
	/// </para>
	/// <para>
	/// What a failure means depends on whether this probe has ever come up in this run. Before the
	/// first success it is a fact about the machine and skips with the exit code; after it, the same
	/// failure is an acceptance test that silently did not run, which is the one outcome this suite
	/// must not report as green. An app that is up but slow costs only the time it actually needs.
	/// </para>
	/// </summary>
	private async Task WaitForProbeWindowAsync(Process child, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

		while (DateTime.UtcNow < deadline)
		{
			if (child.HasExited)
			{
				Unavailable(
					winui.HasLaunched,
					$"The probe app exited with code {child.ExitCode} before it could be attached to, which on WinUI "
						+ "is usually the Windows App Runtime failing to bootstrap.");
			}

			child.Refresh();

			if (child.MainWindowHandle != nint.Zero)
			{
				winui.NoteLaunched();
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		}

		Unavailable(winui.HasLaunched, "The probe app did not open a window within 30 seconds.");
	}
}
