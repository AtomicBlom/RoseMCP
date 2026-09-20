using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The in-app overlay on the classic UWP probe: arming select mode, arming rulers, selecting an
/// element by handle where a click cannot reach it, and what a selection does when its element leaves
/// the tree. <c>overlay.md</c> holds the rules about the capture layer and the marks.
/// <para>
/// Every test here is <c>[ClassicSession]</c>, and again that follows from the concern rather than
/// being imposed on it: the selection and the pointer mode are one per app, so a test that arms a mode
/// needs the app to itself and has to hand it back idle. There is no smaller thing to own.
/// </para>
/// </summary>
[Category("LiveApp")]
[ClassDataSource<UwpProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppUwpOverlayTests(UwpProbeApp probe)
{
	/// <summary>
	/// Interactive selection (#18): every XAML tool leaves RoseMCP's toolbar resident on the app's
	/// diagnostics UI layer, the tree snapshot keeps that toolbar out of its answer, and arming select
	/// mode is reported by the provider rather than assumed here. Until someone clicks, the selection is
	/// empty rather than stale or invented -- the click is a human action, so this stops at the armed
	/// state rather than driving the mouse on a live desktop.
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Arms_interactive_select_mode_on_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// Any XAML tool installs the toolbar, and the tree must not report it: it is RoseMCP's UI,
			// not the app's. Read the tree first so the toolbar is up, then read it again and check.
			var beforeToolbar = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.True(beforeToolbar.Detail is null, $"expected a tree, got detail: {beforeToolbar.Detail}");

			var withToolbar = await session.ReadXamlTreeAsync(cancellationToken);
			Assert.DoesNotContain(withToolbar.Nodes, node => node.Name == "__RoseMcpOverlay");
			Assert.Contains(withToolbar.Nodes, node => node.Name == "Caption");

			// The provider confirms select mode armed, rather than the host assuming it.
			// The framework's own hit test, which is the default and the only sane one: with
			// includeAllElements a background-less Grid stretched over the window shadows every
			// element the user can actually click.
			var selectMode = await session.EnterXamlSelectModeAsync(includeAllElements: false, justMyXaml: true, cancellationToken);
			Assert.True(
				selectMode.Armed,
				$"expected select mode to arm; got: {selectMode.Detail}");
			Assert.True(selectMode.JustMyXaml);
			Assert.Equal("select", selectMode.Mode);

			// Arming reports the preference it was actually given. It used to leave the field to the
			// record's default of true, so arming with false answered true, and a caller comparing the
			// arming response against a later selection saw a contradiction with no explanation. A
			// field session hit exactly that and talked itself out of it with a plausible theory about
			// arm-time preference versus what decided the pick -- which was not what the code did.
			var withoutFilter = await session.EnterXamlSelectModeAsync(includeAllElements: false, justMyXaml: false, cancellationToken);
			Assert.True(withoutFilter.Armed, $"expected select mode to arm; got: {withoutFilter.Detail}");
			Assert.False(withoutFilter.JustMyXaml, "an unfiltered read reports no just-my-xaml filter");

			// And the toolbar agrees, because it is one switch rather than two pieces of state.
			var afterDisabling = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterDisabling.JustMyXaml, "disabling the filter turns it off");

			// Nothing picked yet: an empty selection that says so, safe to poll. Armed comes back from
			// the toolbar's own state file, so this is the provider reporting, not the host remembering.
			var selection = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(selection.Selected, "select mode arms with nothing selected");
			Assert.True(selection.Armed, $"expected the toolbar to report select mode armed; got: {selection.Detail}");
			Assert.NotNull(selection.Detail);

			// #45: the pick can be cleared, and clearing says which of "cleared" and "there was nothing
			// selected" happened rather than treating both as success. Nothing has been picked here --
			// a click is a human action and this suite does not drive the mouse on a live desktop -- so
			// the second is the honest answer, and it is the one that used to be unreachable at all.
			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected, "deselecting clears the selection");
			Assert.Contains("nothing", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

			// And the toolbar is still there afterwards, because deselecting is not leaving.
			var afterClearing = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.False(afterClearing.Selected, "clearing again leaves nothing selected");

			// Armed is app-wide state this test turned on, so this test turns it off. Clearing the
			// pick does not disarm, because they are two pieces of state -- and until the shared app
			// made it matter, nothing here could disarm at all: there was no verb for it, so an agent
			// that armed select mode left a pointer-capturing overlay on the app that only a person
			// clicking Idle could lift.
			var idle = await session.EnterXamlSelectModeAsync(
				includeAllElements: false, justMyXaml: true, arm: false, cancellationToken);
			Assert.False(idle.Armed, $"expected select mode to disarm; got: {idle.Detail}");
			Assert.Equal("idle", idle.Mode);

		}
	}

	/// <summary>
	/// #19: rulers mode arms, anchors on an element, and gives the app back.
	/// <para>
	/// What a person sees in this mode -- the anchor's margin and padding as bands, and the distances
	/// to whatever the pointer is over -- is not reachable from here, because a hover is a human
	/// action and nothing in this suite moves the mouse on a live desktop. What is reachable is every
	/// path that drawing hangs off: arming, switching between the two modes over one capture layer,
	/// anchoring by handle, and disarming. The drawing runs on the app's UI thread inside those calls,
	/// so a throw in it arrives here as a request the provider never answered.
	/// </para>
	/// <para>
	/// The mode is checked by name rather than through Armed alone, and that is the point of the field:
	/// rulers captures the pointer exactly as select does, so a host that only knew about select would
	/// report an app nobody can click as idle -- and a test that only knew about select would hand one
	/// on to the next test.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Arms_rulers_mode_and_anchors_it_on_an_element()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
			Assert.NotNull(pane);

			var rulers = await session.EnterXamlSelectModeAsync(
				includeAllElements: false, justMyXaml: true, arm: true, cancellationToken, mode: "rulers");

			Assert.True(rulers.Armed, $"expected rulers mode to arm; got: {rulers.Detail}");
			Assert.Equal("rulers", rulers.Mode);

			// The toolbar agrees, because the mode is read back from the overlay rather than echoed
			// from the request.
			var armed = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.Equal("rulers", armed.Mode);
			Assert.True(armed.Armed);

			// Anchoring, which is what the bands are drawn around. By handle rather than by clicking,
			// for the same reason #46 exists: a click is a human action. The mode survives it -- unlike
			// select, where picking is the end of the mode -- because a sweep across an element's
			// neighbours is the whole gesture.
			var anchored = await session.SelectXamlElementAsync(pane!.Handle, cancellationToken);
			Assert.True(anchored.Selected, $"expected an anchor; got: {anchored.Detail}");
			Assert.Equal("Pane", anchored.Name);
			Assert.Equal("rulers", anchored.Mode);

			// Switching modes keeps the one capture layer that is already up, so arming select from
			// here has to answer about a layer it did not insert.
			var select = await session.EnterXamlSelectModeAsync(
				includeAllElements: false, justMyXaml: true, arm: true, cancellationToken, mode: "select");
			Assert.Equal("select", select.Mode);
			Assert.True(select.Armed, $"expected select mode to arm over the layer already up; got: {select.Detail}");

			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected, "deselecting clears the anchor");

			var idle = await session.EnterXamlSelectModeAsync(
				includeAllElements: false, justMyXaml: true, arm: false, cancellationToken);
			Assert.False(idle.Armed, $"expected the overlay to go idle; got: {idle.Detail}");
			Assert.Equal("idle", idle.Mode);

		}
	}

	/// <summary>
	/// #46: selecting by handle, with no hit test in the path. That is what reaches a control a click
	/// cannot -- a slider is the reported case, and what a click resolves to is the framework's answer
	/// rather than ours -- and it is the only way this suite can make a selection at all, since a
	/// click is a human action and nothing here drives the mouse on a live desktop.
	/// <para>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Selects_a_xaml_element_by_handle_without_a_click()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// The handle comes from the tree, which is the whole route this exists to open.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var pane = tree.Nodes.FirstOrDefault(node => node.Name == "Pane");
			Assert.NotNull(pane);

			var selected = await session.SelectXamlElementAsync(pane!.Handle, cancellationToken);

			Assert.True(selected.Selected, $"expected a selection; got: {selected.Detail}");
			Assert.Equal(pane.Handle, selected.Handle);
			Assert.Equal("Pane", selected.Name);

			// The stack is the element then its ancestors outwards, so a caller who took the handle the
			// tree gave them can still reach the container they actually meant.
			Assert.Contains(selected.Candidates, candidate => candidate.Name == "Panel");
			Assert.Contains(selected.Candidates, candidate => candidate.Name == "RootGrid");

			// It reads back through the same path a click produces, which is the point of writing the
			// same files: one read path, whichever route made the selection.
			var reread = await session.ReadXamlSelectionAsync(cancellationToken);
			Assert.True(reread.Selected);
			Assert.Equal(pane.Handle, reread.Handle);

			// And the handle it hands back drives the rest of the surface without another round trip.
			var properties = await session.ReadXamlPropertiesAsync(selected.Handle, includeDefaults: false, cancellationToken);
			Assert.True(properties.Detail is null, $"expected properties, got detail: {properties.Detail}");
			Assert.Contains(properties.Properties, property => property.Name == "CornerRadius");

			// Clearing it works the same as for a click, because it is the same selection (#45).
			var cleared = await session.ClearXamlSelectionAsync(cancellationToken);
			Assert.False(cleared.Selected, "deselecting clears a selection made by handle");
			Assert.Contains("cleared", cleared.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

		}
	}

	/// <summary>
	/// A handle that names something real but not an element -- a Brush has one too -- is refused with
	/// a sentence rather than drawing an outline round nothing.
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Refuses_to_select_a_handle_that_is_not_an_element()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			await session.ReadXamlTreeAsync(cancellationToken);

			// A handle nothing owns. The provider resolves it, finds nothing, and declines.
			var selected = await session.SelectXamlElementAsync(1, cancellationToken);

			Assert.False(selected.Selected, "a refused selection selects nothing");
			Assert.NotNull(selected.Detail);

		}
	}

	/// <summary>
	/// #51: a selection whose element leaves the visual tree is cleared, and says why.
	/// <para>
	/// The selection is the one mark that outlives the interaction which drew it, so it was the one
	/// mark with nothing watching it -- the outline stayed where it was while pointing at nothing, and
	/// the recorded handle stayed too, so the next properties call failed with a diagnostics HRESULT
	/// instead of "the thing you picked no longer exists".
	/// </para>
	/// <para>
	/// The probe's Transient border leaves the tree and comes back on a cycle, announcing each
	/// departure in the event stream. It re-adds the same instance on purpose, which is what
	/// virtualization and a rebuilt panel do -- so the handle stays valid across the removal, and
	/// nothing about it can be used as a liveness test.
	/// </para>
	/// <para>
	/// Testable at all only because of #46: a click is a human action, and this suite cannot make one.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Clears_a_selection_whose_element_leaves_the_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		{

			// Transient is only in the tree for part of its cycle, so finding it is a retry rather than
			// a single read. Selecting it is the same call, because a select on something with no
			// bounds is refused rather than half-applied.
			var selected = await SelectTransientAsync(session, cancellationToken);
			Assert.True(selected.Selected, $"expected to select Transient; got: {selected.Detail}");
			Assert.Equal("Transient", selected.Name);

			// Where the stream is now, taken before the app is given a chance to act, so the removal
			// waited for below is the one that takes this pick away. The app announces a removal every
			// five seconds whether anything is selected or not, and this session is shared by every
			// test in the class, so a wait from the start of the stream matches a removal from before
			// the pick, returns without waiting, and leaves every assertion under it running against a
			// selection the app has not touched.
			var since = await CursorNowAsync(session, cancellationToken);

			// Now wait for the app to take it away. The exception is the only channel out of the app.
			var removed = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpTransientRemovedException") ?? false),
				cancellationToken,
				startCursor: since);
			Assert.NotNull(removed);

			// The provider clears on the removal callback, which arrives on the app's UI thread as the
			// removal happens -- so by the time the exception has been observed the work is done.
			var after = await session.ReadXamlSelectionAsync(cancellationToken);

			Assert.False(after.Selected, "an element leaving the tree clears the selection");
			Assert.Equal(0ul, after.Handle);
			Assert.Contains("removed from the visual tree", after.Detail ?? string.Empty, StringComparison.Ordinal);

		}
	}

	/// <summary>
	/// Selects the probe's Transient border, waiting for one of its in-tree phases. Absent is the
	/// expected answer some of the time, and a select against an element with no bounds is refused,
	/// so both are retried rather than treated as failures.
	/// </summary>
	private static async Task<LiveXamlSelection> SelectTransientAsync(
		LiveAppSession session,
		CancellationToken cancellationToken)
	{
		LiveXamlSelection last = new() { Detail = "Transient never appeared in the tree." };

		for (var attempt = 0; attempt < 20; attempt++)
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			if (tree.Nodes.FirstOrDefault(node => node.Name == "Transient") is { } transient)
			{
				last = await session.SelectXamlElementAsync(transient.Handle, cancellationToken);
				if (last.Selected) return last;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
		}

		return last;
	}
}
