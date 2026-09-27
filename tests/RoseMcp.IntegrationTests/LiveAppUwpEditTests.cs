using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.ProbeTargetSession;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The apply path against the classic UWP probe: property edits, structural add and remove, attached
/// properties, keyed resources, and the edit-and-apply loop over a file. The rules these hold to are
/// in <c>xaml-live-edit.md</c>, which is worth reading before changing any of them -- most of what is
/// asserted here is there because getting it wrong produced a confident wrong answer rather than a
/// failure.
/// <para>
/// The only group that spans all three lease phases, and the split says why: an edit inside a scratch
/// slot is <c>[ClassicSlot]</c> and overlaps, one that rebuilds the app's resource dictionary is
/// <c>[ClassicSession]</c> because a dictionary has no owner smaller than the app, and one that drives
/// a file through successive applies takes the app outright.
/// </para>
/// </summary>
[Category("ProbeApp")]
[ClassDataSource<UwpProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppUwpEditTests(UwpProbeApp probe)
{
	/// <summary>
	/// XAML live editing (#12): diff two versions of the probe's XAML and apply the changes to the live
	/// tree, no relaunch. Two edits, deliberately, because they fail in different ways:
	/// <list type="bullet">
	/// <item>
	/// The caption's font size is a Double, the straightforward case, and it is confirmed by reading
	/// the live value back.
	/// </item>
	/// <item>
	/// The pane's corner radius is a struct whose value is a single number, which the diff engine's
	/// name-and-shape inference called a Double until it was told otherwise -- and a value built as
	/// the wrong type is created quite happily and only fails at SetProperty, with an E_FAIL that
	/// names nothing. This is the end-to-end guard for that, and for the provider's fallback to the
	/// property's own declared type. It is asserted by reading the value back as well as through
	/// its status: reading it back was impossible until #21, because the framework hands us an
	/// empty string for a CornerRadius value where Thickness stringifies.
	/// </item>
	/// </list>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </summary>
	[Test]
	[ClassicSlot(3)]
	public async Task Live_edits_a_property_on_the_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C: both edits land on elements this test built in its own slot, so it neither disturbs
		// the app's furniture nor depends on that furniture still carrying the values the markup gave
		// it. The two kinds of value are the point -- a Double and a struct -- not which elements
		// happen to carry them.
		const string Before =
			"<TextBlock Text=\"Rose UWP Probe\" FontSize=\"24\" />"
				+ "<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />";
		const string After =
			"<TextBlock Text=\"Rose UWP Probe\" FontSize=\"40\" />"
				+ "<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"0\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Before), filePath: null, cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Before), turn.MarkupHolding(After), filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("TextBlock[0]") && result.Property == "FontSize");
			edit.ShouldNotBeNull();
			edit!.Status.ShouldBe("applied");

			// The struct-valued edit, which is the one that used to come back "SetProperty failed
			// 0x80004005" because it had been built as a Double.
			var radius = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("Border[0]") && result.Property == "CornerRadius");
			radius.ShouldNotBeNull();
			radius!.Status.ShouldBe("applied");

			applied.Applied.ShouldBe(2);

			// The live element actually changed: reading its font size back gives the new value.
			var tree = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var caption = tree.Nodes.Single(node => node.Address == turn.Address("TextBlock[0]"));
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			var fontSize = properties.Properties.FirstOrDefault(property => property.Name == "FontSize");
			fontSize.ShouldNotBeNull();
			fontSize!.Value.ShouldBe("40");

			// And the struct-valued edit is now read back too, rather than trusted from its status.
			// It used to be asserted only through "applied", because a CornerRadius came back as an
			// empty string -- which is #21, and is fixed, so the weaker assertion has no reason left.
			var pane = tree.Nodes.Single(node => node.Address == turn.Address("Border[0]"));
			var paneProperties = await session.ReadXamlPropertiesAsync(pane.Handle, includeDefaults: false, cancellationToken);
			var cornerRadius = paneProperties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");
			cornerRadius.ShouldNotBeNull();
			cornerRadius!.Value.ShouldBe("0,0,0,0");
		}
	}

	/// <summary>
	/// A value holding the characters the wire is delimited by reaches the app intact, and its status says
	/// so. Escaped on the way back and not on the way out, a tab in a value moved every field after it and
	/// a newline split one command into two -- and since a result is found again by the fields it was
	/// sent with, the edit that half-landed then reported that it was never applied. The value is read back
	/// rather than trusted from the status, because that failure was a status that lied.
	/// <para>
	/// A backslash is in it too, because the escape character is the one an escape table most easily
	/// leaves out: unescaped on the way out and read as an escape on the way in, <c>three\four</c> reaches
	/// the app as <c>threefour</c>, and a tab and a newline alone would not show it.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(7)]
	public async Task Applies_a_value_holding_a_tab_a_newline_and_a_backslash_and_reads_it_back_intact()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Character references, because an XML parser turns a literal tab or newline inside an attribute
		// into a space before the diff ever sees it.
		const string Before = "<TextBlock Text=\"plain\" />";
		const string After = "<TextBlock Text=\"one&#9;two&#10;three\\four\" />";
		const string Expected = "one\ttwo\nthree\\four";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Before), filePath: null, cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Before), turn.MarkupHolding(After), filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("TextBlock[0]") && result.Property == "Text");
			edit.ShouldNotBeNull();
			edit!.Status.ShouldBe("applied");

			var tree = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var caption = tree.Nodes.Single(node => node.Address == turn.Address("TextBlock[0]"));
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			(properties.Properties.FirstOrDefault(property => property.Name == "Text")?.Value).ShouldBe(Expected);
		}
	}

	/// <summary>
	/// Addressing an element the markup never named (#11). Every element in the probe app used to carry
	/// an <c>x:Name</c>, so nothing here could reach the case that matters most: a click inside a
	/// control template lands on an element with no name, and until now there was no way to say which
	/// element was meant -- the apply refused it, and a caller only found that out after composing a
	/// whole before-and-after XAML pair. <c>#Pair/Border[1]</c> is the way to say it.
	/// <para>
	/// The negative assertion is the one that earns the test. Resolving <em>an</em> element proves
	/// nothing, because the failure this replaces put the change on a plausible neighbour and reported
	/// success either way -- so the first Border is read back as well, and has to be untouched.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(4)]
	public async Task Live_edits_an_unnamed_element_by_its_address()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// The second of two unnamed siblings, which is the case this exists for: an element the markup
		// never named, told from its twin by position under a named anchor. The anchor is this test's
		// own slot rather than the app's Pair, so the two Borders it counts are the only two there.
		const string FirstBackground = "#FF3A2A2A";
		const string SecondBackground = "#FF2A3A2A";
		const string ChangedBackground = "#FF00FF00";
		const string Pair =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"" + SecondBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";
		const string Changed =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"" + ChangedBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Pair), filePath: null, cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			// The provider derives an address from the live tree, so this half stands on its own: two
			// unnamed siblings of one type are told apart by their position under the named anchor.
			var before = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var addresses = before.Nodes.Select(node => node.Address).ToList();
			addresses.ShouldContain(turn.Address("Border[0]"));
			addresses.ShouldContain(turn.Address("Border[1]"));

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Pair), turn.MarkupHolding(Changed), filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(
				result => result.Target == turn.Address("Border[1]") && result.Property == "Background");
			edit.ShouldNotBeNull();
			edit!.Status.ShouldBe("applied");

			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			(await BackgroundAtAsync(session, tree, turn.Address("Border[1]"), cancellationToken)).ShouldBe(ChangedBackground);
			(await BackgroundAtAsync(session, tree, turn.Address("Border[0]"), cancellationToken)).ShouldBe(FirstBackground);
		}
	}

	/// <summary>
	/// Removing an element live (#11). <c>IVisualTreeService::RemoveChild</c> takes a parent and a
	/// <em>position</em>, while a diff knows only that a child present in the old markup is absent from
	/// the new one -- so the provider resolves the child's address and reads the parent and the index off
	/// the live tree, which is the only place they can be trusted.
	/// <para>
	/// Which Border survives is the assertion that earns this. The two are identical but for their
	/// colour, so an index off by one removes the wrong one and everything else still reads as success:
	/// one Border left under the anchor, the edit reported <c>applied</c>, and the wrong element gone.
	/// The survivor's Background is what tells them apart.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(5)]
	public async Task Removes_an_element_from_the_live_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Two unnamed siblings in this test's own slot, and the second one goes. Built here rather than
		// cut out of the app's markup: a removal is the edit that most needs to be nobody else's, since
		// the index it resolves against is a position among siblings.
		//
		// It used to take the app exclusively as well, because it passed alone and failed in company
		// reporting the removal applied while both Borders were still there. That was never a
		// concurrency problem and exclusivity never fixed it, only hid it: the fixture's own slot
		// cleanup emitted its two removals in document order, the first renumbered the second, and the
		// slot was handed on still holding an element this test then counted as one of its own. The
		// ordering is fixed in XamlDiff and the cleanup checks itself, so a slot is enough.
		const string FirstBackground = "#FF3A2A2A";
		const string Pair =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />"
				+ "<Border Background=\"#FF2A3A2A\" Padding=\"6\" CornerRadius=\"3\" />";
		const string Survivor =
			"<Border Background=\"" + FirstBackground + "\" Padding=\"6\" CornerRadius=\"3\" />";

		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup, turn.MarkupHolding(Pair), filePath: null, cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			var before = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			before.Nodes.Count(node => node.Address == turn.Address("Border[0]") || node.Address == turn.Address("Border[1]")).ShouldBe(2);

			var applied = await session.ApplyXamlAsync(
				turn.MarkupHolding(Pair), turn.MarkupHolding(Survivor), filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var removal = applied.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			removal.ShouldNotBeNull();
			removal!.Target.ShouldBe(turn.Address("Border[1]"));
			removal.Status.ShouldBe("applied");

			// Read back off a fresh enumeration, so this checks the app rather than the provider's own
			// bookkeeping: every injection builds a new tap and walks the tree again.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var remaining = tree.Nodes
				.Where(node => node.Address == turn.Address("Border[0]") || node.Address == turn.Address("Border[1]"))
				.ToList();
			var survivor = remaining.ShouldHaveSingleItem();
			survivor.Address.ShouldBe(turn.Address("Border[0]"));

			// And it is the one that was meant to stay.
			(await BackgroundAtAsync(session, tree, turn.Address("Border[0]"), cancellationToken)).ShouldBe(FirstBackground);

		}
	}

	/// <summary>
	/// The card's acceptance criterion for #11, whole: a diff that adds a child, removes a child and
	/// changes a non-brush property, applied live in one call.
	/// <para>
	/// Adding is the piece that cannot be one command. There is no way to apply markup -- CreateInstance
	/// builds one object from a type name -- so the subtree is decomposed into build steps and the
	/// element is assembled off the tree before anything attaches it. What this checks is that the
	/// assembled element arrives complete: it is not enough for it to exist, so its own property is read
	/// back off the running app, and so is the nested child it was given.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Adds_removes_and_retypes_in_one_apply()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		// The added element is deliberately a different type from the removed one. A diff is minimal, so
		// swapping a Border for a Border is a property change and no structural edit happens at all --
		// which is right, and not what this test is for.
		//
		// The two types are chosen to exercise both halves of resolving a name for CreateInstance. The app
		// already has a Grid, so that one is answered from the types the live tree reports; it has no
		// Rectangle anywhere, and Shapes is not the namespace Controls live in, so that one can only come
		// from the framework namespaces tried afterwards.
		const string SecondBorder = "<Border Background=\"#FF2A3A2A\"";
		const string Added =
			"<Grid Background=\"#FF00FFFF\"><Rectangle Fill=\"#FFFF00FF\" Width=\"10\" Height=\"10\" /></Grid>";

		var start = oldXaml.IndexOf(SecondBorder, StringComparison.Ordinal);
		start.ShouldBeGreaterThanOrEqualTo(0, "the probe markup no longer holds the second unnamed Border");
		var close = oldXaml.IndexOf("</Border>", start, StringComparison.Ordinal) + "</Border>".Length;

		var newXaml = oldXaml
			.Remove(start, close - start)
			.Insert(start, Added)
			.Replace("Text=\"ticks: 0\"", "Text=\"ticks: 0\" Opacity=\"0.5\"");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp add probe",
				},
				cancellationToken);

			session.Describe().State.ShouldBe(LiveAppSessionState.Ready);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			running.ShouldNotBeNull();

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var add = applied.Results.FirstOrDefault(result => result.Kind == "AddChild");
			add.ShouldNotBeNull();
			add!.Status.ShouldBe("applied");

			var removal = applied.Results.FirstOrDefault(result => result.Kind == "RemoveChild");
			removal.ShouldNotBeNull();
			removal!.Status.ShouldBe("applied");

			// The non-brush property, on a named element, so all three kinds are in the one apply.
			var opacity = applied.Results.FirstOrDefault(result => result.Property == "Opacity");
			opacity.ShouldNotBeNull();
			opacity!.Status.ShouldBe("applied");

			// The added element is in the app, and it arrived built rather than merely present: its own
			// property is set, and the child it was given is underneath it. Existing is not complete.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			(await BackgroundAtAsync(session, tree, "#Pair/Grid[0]", cancellationToken)).ShouldBe("#FF00FFFF");

			var nested = tree.Nodes.SingleOrDefault(node => node.Address == "#Pair/Grid[0]/Rectangle[0]");
			nested.ShouldNotBeNull();
			nested!.TypeName.ShouldEndWith("Rectangle", Case.Sensitive);

			// And the removal happened: one Border left under the anchor, not two.
			tree.Nodes.Where(node => node.Address is "#Pair/Border[0]" or "#Pair/Border[1]").ShouldHaveSingleItem();

			(await manager.CloseAsync(session.SessionId, cancellationToken)).ShouldBeTrue();
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// Attached properties on the apply side (#11). The diff has always kept <c>Grid.Row</c> by its
	/// dotted name, which is a different question from whether the provider can find one: an attached
	/// property is not declared on the element it is set on, so whether it turns up in that element's
	/// property chain under the same spelling the markup used is a fact about the framework and had
	/// never been asked.
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Sets_an_attached_property_on_the_live_tree()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		oldXaml.ShouldContain("Grid.Row=\"0\"", Case.Sensitive);
		var newXaml = oldXaml.Replace("Grid.Row=\"0\"", "Grid.Row=\"1\"");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var session = await manager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = aumid,
					Description = "uwp attached probe",
				},
				cancellationToken);

			session.Describe().State.ShouldBe(LiveAppSessionState.Ready);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			running.ShouldNotBeNull();

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(result => result.Property == "Grid.Row");
			edit.ShouldNotBeNull();
			edit!.Target.ShouldBe("#Attached");
			edit.Status.ShouldBe("applied");

			// Read back off the app, because "applied" only says SetProperty returned S_OK.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var element = tree.Nodes.Single(node => node.Name == "Attached");
			var properties = await session.ReadXamlPropertiesAsync(element.Handle, includeDefaults: false, cancellationToken);
			var row = properties.Properties.FirstOrDefault(property => property.Name == "Grid.Row");
			row.ShouldNotBeNull();
			row!.Value.ShouldBe("1");

			(await manager.CloseAsync(session.SessionId, cancellationToken)).ShouldBeTrue();
		}
		finally
		{
			probe.StopApp();
		}
	}

	/// <summary>
	/// Resource-dictionary edits (#11), the last thing on the card. A resource is keyed rather than
	/// positional, and it is not an element in the visual tree at all -- a <c>*.Resources</c> block is a
	/// property written in element form -- so before this the diff addressed one as
	/// <c>Grid[0]/Grid.Resources[0]/SolidColorBrush[0]</c> and the apply failed complaining about a
	/// missing element, which is the wrong problem stated confidently.
	/// <para>
	/// The assertion that matters is the second one: the element already using the key. Replacing what a
	/// key resolves to is only worth anything if what was drawn from it follows, and that is a fact
	/// about the framework rather than about this code -- which is why the probe references the brush
	/// with <c>ThemeResource</c>, the form that re-evaluates.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Replaces_a_keyed_resource_on_the_live_tree()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: holds the shared probe app to itself, because what this touches is app-wide
		// and has no owner smaller than the app. The turn checks on the way out that the app was
		// handed back unselected and unarmed.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		const string Was = "#FF445566";
		const string Now = "#FFAA3300";
		oldXaml.ShouldContain($"x:Key=\"ProbeAccent\" Color=\"{Was}\"", Case.Sensitive);
		var newXaml = oldXaml.Replace($"x:Key=\"ProbeAccent\" Color=\"{Was}\"", $"x:Key=\"ProbeAccent\" Color=\"{Now}\"");

		{

			session.Describe().State.ShouldBe(LiveAppSessionState.Ready);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			running.ShouldNotBeNull();

			// The element is drawing the old colour through the key before anything is changed.
			var before = await session.ReadXamlTreeAsync(cancellationToken);
			(await BackgroundAtAsync(session, before, "#Themed", cancellationToken)).ShouldBe(Was);

			var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
			(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

			var edit = applied.Results.FirstOrDefault(result => result.Kind == "SetResource");
			edit.ShouldNotBeNull();
			edit!.Target.ShouldBe("#RootGrid");
			edit.Property.ShouldBe("ProbeAccent");
			edit.Status.ShouldBe("applied");

			// And the element that resolves the key follows it.
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			(await BackgroundAtAsync(session, tree, "#Themed", cancellationToken)).ShouldBe(Now);

		}
	}

	/// <summary>
	/// A template in a resource dictionary, which is the one resource shape a live edit cannot rebuild.
	/// A resource is applied by building it out of <c>CreateInstance</c> and <c>AddChild</c> and then
	/// swapping what its key resolves to; a template's content is compiled markup, and the property
	/// chain of a built one holds nothing either of those can put it into.
	/// <para>
	/// Two template types rather than one, because the refusal is a rule about a family: measured
	/// against a <c>DataTemplate</c> alone it would be a rule validated only by the case that motivated
	/// it, and <c>ControlTemplate</c> is the nearest thing it also decides.
	/// </para>
	/// <para>
	/// What matters is that it is said. Left to fail somewhere inside the template, the outcome arrives
	/// as a <c>SetResource</c> row whose property is the resource key -- so a status of "property not
	/// found" reads as the key having been looked up as a property on the element that owns the
	/// dictionary, a confident wrong account of what went wrong. Item templates are also where most of a
	/// list-heavy app's markup lives, so an unexplained empty apply there reads as live editing not
	/// working on the view at all.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSession]
	public async Task Says_a_template_resource_cannot_be_rebuilt_live()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase B: a resource dictionary is app-wide and has no owner smaller than the app. Nothing here
		// should reach it -- that is the assertion -- but an apply that got as far as ReplaceResource
		// would, and a test cannot both check that and share the app.
		await using var turn = await probe.TakeSessionAsync(cancellationToken);
		var session = turn.Session;

		var xamlPath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var oldXaml = File.ReadAllText(xamlPath);

		const string DataTemplateWas = "<Border Background=\"#FF556677\" Padding=\"4\">";
		const string ControlTemplateWas = "<Border Background=\"#FF667788\" Padding=\"4\" />";
		oldXaml.ShouldContain(DataTemplateWas, Case.Sensitive);
		oldXaml.ShouldContain(ControlTemplateWas, Case.Sensitive);

		var newXaml = oldXaml
			.Replace(DataTemplateWas, "<Border Background=\"#FF991122\" Padding=\"4\">")
			.Replace(ControlTemplateWas, "<Border Background=\"#FF223399\" Padding=\"4\" />");

		var applied = await session.ApplyXamlAsync(oldXaml, newXaml, filePath: null, cancellationToken);
		(applied.Detail is null).ShouldBeTrue($"expected an apply, got detail: {applied.Detail}");

		var reported = string.Join(
			" | ",
			applied.Results.Select(result => $"{result.Kind} '{result.Property}' on {result.Target}: {result.Status}"));

		applied.Results.Count.ShouldBe(0,
			$"a template resource has no live edit to report, and this reported: {reported}");

		var notes = string.Join(" | ", applied.Notes);
		foreach (var (key, type) in new[] { ("ProbeItemTemplate", "DataTemplate"), ("ProbeControlTemplate", "ControlTemplate") })
		{
			applied.Notes.Any(note =>
					note.Contains(key, StringComparison.Ordinal) && note.Contains(type, StringComparison.Ordinal)).ShouldBeTrue(
				$"a note has to name {key} and say it is a {type}; notes were: {notes}");
		}
	}

	/// <summary>The Background of whichever live element carries an address, read off the tree.</summary>
	private static async Task<string?> BackgroundAtAsync(
		LiveAppSession session,
		LiveXamlTree tree,
		string address,
		CancellationToken cancellationToken)
	{
		var node = tree.Nodes.Single(candidate => candidate.Address == address);
		var properties = await session.ReadXamlPropertiesAsync(node.Handle, includeDefaults: false, cancellationToken);
		return properties.Properties.FirstOrDefault(property => property.Name == "Background")?.Value;
	}

	/// <summary>
	/// The edit-to-live loop (#12): edit a XAML file, apply, edit it again, apply again, and the running
	/// app follows -- with nothing carried between the calls but the path of the file.
	/// <para>
	/// Every apply test before this one passed both versions of the markup, which is the shape the tool
	/// started with and close to unusable in the loop it exists for, since a caller that has just
	/// written a file no longer holds what was in it. So the session remembers what it last sent, and
	/// the assertion that earns this test is the <em>count</em> on the second edit: it changes a
	/// different property from the first, so a baseline still sitting on the original would come back
	/// with two edits rather than one. Re-sending an edit is harmless for a font size and, for an added
	/// element, a second copy of it.
	/// </para>
	/// <para>
	/// It edits a copy of the probe's markup rather than the file itself. What is under test is the
	/// file-to-diff-to-apply-to-live path, which a copy exercises identically; editing the fixture in
	/// place would leave a tracked file modified if this failed part way through, and the sibling tests
	/// read that file expecting what is checked in.
	/// </para>
	/// </summary>
	[Test]
	[ClassicOwnApp]
	public async Task Applies_successive_file_edits_to_the_running_app()
	{
		using var lease = await probe.LeaseAsync(needsXamlProvider: true, TestContext.Current!.Execution.CancellationToken);
		var aumid = lease.Aumid;

		var sourcePath = Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml");
		var original = File.ReadAllText(sourcePath);
		var editable = Path.Combine(Path.GetTempPath(), $"rose-reload-{Guid.NewGuid():N}.xaml");

		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		try
		{
			var target = new LiveAppTarget
			{
				Kind = LiveAppTargetKind.LaunchUwp,
				AppUserModelId = aumid,
				Description = "uwp continuous live-edit probe",
			};

			var session = await manager.StartAsync(target, cancellationToken);
			session.Describe().State.ShouldBe(LiveAppSessionState.Ready);

			var running = await WaitForEventAsync(
				session,
				entry => entry.Kind == LiveDebugEventKind.ExceptionFirstChance
					&& (entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false),
				cancellationToken);
			running.ShouldNotBeNull();

			// The app's own markup, untouched since it was launched. There is nothing to apply, and
			// this side says so with evidence rather than by diffing the file against itself -- which
			// would report nothing either, and would mean something else entirely.
			var first = await session.ApplyXamlAsync(null, null, sourcePath, cancellationToken);
			(first.Detail is null).ShouldBeTrue($"expected a baseline, got detail: {first.Detail}");
			first.Results.ShouldBeEmpty();
			first.Notes.ShouldContain(note => note.Contains("Nothing has edited") && note.Contains("MainPage.xaml"));

			// A file that has changed since the app started is the other first-apply case: what the
			// app was built from is gone, so it records the file and says so rather than guessing.
			File.WriteAllText(editable, original);
			var registered = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			(registered.Detail is null).ShouldBeTrue($"expected a baseline, got detail: {registered.Detail}");
			registered.Results.ShouldBeEmpty();
			registered.Notes.ShouldContain(note => note.Contains("no longer on disk"));

			// One edit, applied with nothing passed but the path.
			File.WriteAllText(editable, original.Replace("FontSize=\"24\"", "FontSize=\"40\""));
			var fontSize = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			(fontSize.Detail is null).ShouldBeTrue($"expected an apply, got detail: {fontSize.Detail}");
			var sizeEdit = fontSize.Results.ShouldHaveSingleItem();
			sizeEdit.Target.ShouldBe("#Caption");
			sizeEdit.Property.ShouldBe("FontSize");
			sizeEdit.Status.ShouldBe("applied");
			(await CaptionValueAsync(session, "FontSize", cancellationToken)).ShouldBe("40");

			// A second edit, same session, no relaunch -- and a different property, which is what makes
			// the single result below mean the baseline moved with the first apply.
			File.WriteAllText(
				editable,
				original
					.Replace("FontSize=\"24\"", "FontSize=\"40\"")
					.Replace("Text=\"Rose UWP Probe\"", "Text=\"Edited twice\""));

			var caption = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			(caption.Detail is null).ShouldBeTrue($"expected an apply, got detail: {caption.Detail}");
			var textEdit = caption.Results.ShouldHaveSingleItem();
			textEdit.Target.ShouldBe("#Caption");
			textEdit.Property.ShouldBe("Text");
			textEdit.Status.ShouldBe("applied");

			// Both edits are on the running app: the second landed, and the first is still there rather
			// than having been undone by a diff that started over from the original.
			(await CaptionValueAsync(session, "Text", cancellationToken)).ShouldBe("Edited twice");
			(await CaptionValueAsync(session, "FontSize", cancellationToken)).ShouldBe("40");

			// And an apply with nothing to apply says which of the two nothings it was.
			var unchanged = await session.ApplyXamlAsync(null, null, editable, cancellationToken);
			(unchanged.Detail is null).ShouldBeTrue($"expected an apply, got detail: {unchanged.Detail}");
			unchanged.Results.ShouldBeEmpty();
			unchanged.Notes.ShouldContain(note => note.Contains("unchanged since the last apply"));

			(await manager.CloseAsync(session.SessionId, cancellationToken)).ShouldBeTrue();
		}
		finally
		{
			probe.StopApp();
			if (File.Exists(editable)) File.Delete(editable);
		}
	}

	/// <summary>One of the probe caption's live property values, read off the running app.</summary>
	private static async Task<string?> CaptionValueAsync(
		LiveAppSession session,
		string property,
		CancellationToken cancellationToken)
	{
		var tree = await session.ReadXamlTreeAsync(cancellationToken);
		var caption = tree.Nodes.First(node => node.Name == "Caption");
		var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);

		return properties.Properties.FirstOrDefault(candidate => candidate.Name == property)?.Value;
	}
}
