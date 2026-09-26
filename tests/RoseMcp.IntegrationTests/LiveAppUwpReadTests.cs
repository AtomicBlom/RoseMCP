using RoseMcp.Broker;
using RoseMcp.TestSupport;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Reading the classic UWP probe's live XAML: the visual tree, and one element's properties with the
/// provenance and source position that make a live element findable in its markup.
/// <para>
/// Every test here is <c>[ClassicSlot]</c>, and that is the concern rather than a scheduling detail: a
/// read changes nothing, so these need the shared app but no part of it to themselves, and they
/// overlap each other. See <see cref="ProbeKeys"/> for what a slot key buys.
/// </para>
/// </summary>
[Category("ProbeApp")]
[ClassDataSource<UwpProbeApp>(Shared = SharedType.PerAssembly)]
public sealed class LiveAppUwpReadTests(UwpProbeApp probe)
{
	/// <summary>
	/// The XAML track's first vertical (#2/#3, seed of #9): launch the classic UWP probe, inject the
	/// diagnostics provider, and read its live visual tree. Proves the provider builds, injects into the
	/// AppContainer, enumerates on the UI thread, and reports the tree back through the host to the
	/// broker. Skips where the UWP build toolchain or the C++ toolset is absent.
	/// </summary>
	[Test]
	[ClassicSlot(0)]
	public async Task Reads_the_live_visual_tree_of_the_classic_uwp_probe()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, reading only. It asserts on the elements the markup declares, so it neither needs a
		// slot nor minds one being busy: what it looks for is furniture, and no phase C test edits
		// anything outside its own slot.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			(tree.Detail is null).ShouldBeTrue($"expected a tree, got detail: {tree.Detail}");
			tree.Nodes.ShouldNotBeEmpty();

			// The probe's named elements are all present, addressable from the flat parent/child list.
			foreach (var name in new[] { "RootGrid", "Panel", "Pane", "Counter", "Caption" })
			{
				tree.Nodes.ShouldContain(node => node.Name == name);
			}

			// Rooting returns a named element's subtree only (#9): Panel's subtree has its descendants
			// (the caption) but not its parent (RootGrid).
			var panelSubtree = await session.ReadXamlTreeAsync("Panel", offset: 0, limit: 0, cancellationToken);
			panelSubtree.Nodes.ShouldContain(node => node.Name == "Panel");
			panelSubtree.Nodes.ShouldContain(node => node.Name == "Caption");
			panelSubtree.Nodes.ShouldNotContain(node => node.Name == "RootGrid");

			// Paging: a limited page carries at most that many nodes, and Total says how many matched.
			var firstPage = await session.ReadXamlTreeAsync(root: null, offset: 0, limit: 2, cancellationToken);
			firstPage.Nodes.Count.ShouldBe(2);
			firstPage.Total.ShouldBeGreaterThan(2, $"expected more than a page of nodes; total {firstPage.Total}");

			// The three spellings of one element all reach it. The address is the one that matters: it is
			// what an element with no x:Name has instead, which is everything inside a control template,
			// and passing it back was refused for not being a number.
			var caption = tree.Nodes.Single(node => node.Name == "Caption");

			caption.Address.ShouldNotBeNull();
			(await session.ResolveElementAsync(caption.Handle.ToString(), cancellationToken)).ShouldBe(caption.Handle);
			(await session.ResolveElementAsync("#Caption", cancellationToken)).ShouldBe(caption.Handle);
			(await session.ResolveElementAsync("Caption", cancellationToken)).ShouldBe(caption.Handle);
			(await session.ResolveElementAsync(caption.Address!, cancellationToken)).ShouldBe(caption.Handle);

			// And an address roots the tree, which the host itself cannot do -- it knows only names.
			var byAddress = await session.ReadXamlTreeAsync(caption.Address, offset: 0, limit: 0, cancellationToken);

			byAddress.Nodes.ShouldContain(node => node.Handle == caption.Handle);

			var missing = await Should.ThrowAsync<ArgumentException>(
				() => session.ResolveElementAsync("#NotThere", cancellationToken)).OfExactType();

			missing.Message.ShouldContain("Nothing in the live tree is called", Case.Sensitive);
		}
	}

	/// <summary>
	/// XAML property inspection (#10): read an element's properties with provenance. Confirms the set
	/// (non-default) properties come back with a Local provenance for what the XAML sets -- including a
	/// concrete string value -- that framework defaults are filtered out unless asked for, and that it
	/// all rides through the host to the broker. Skips where the UWP or C++ toolchain is absent.
	/// </summary>
	[Test]
	[ClassicSlot(1)]
	public async Task Reads_the_properties_of_a_xaml_element()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, and one that reads the app's declared furniture rather than building its own. It
		// has to: half of what it asserts is source info -- which file and line declared the element --
		// and an element this test created at runtime has none, because nothing declared it. So the
		// slot goes unused here, and what makes this safe is the other half of the bargain: no phase C
		// test edits anything outside its own slot.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var tree = await session.ReadXamlTreeAsync(cancellationToken);
			var rootGrid = tree.Nodes.FirstOrDefault(node => node.Name == "RootGrid");
			var caption = tree.Nodes.FirstOrDefault(node => node.Name == "Caption");
			rootGrid.ShouldNotBeNull();
			caption.ShouldNotBeNull();

			var properties = await session.ReadXamlPropertiesAsync(rootGrid!.Handle, includeDefaults: false, cancellationToken);
			(properties.Detail is null).ShouldBeTrue($"expected properties, got detail: {properties.Detail}");
			properties.Properties.ShouldNotBeEmpty();

			// Background is set in the probe's XAML, so it reads back with Local provenance...
			var background = properties.Properties.FirstOrDefault(property => property.Name == "Background");
			background.ShouldNotBeNull();
			background!.Provenance.ShouldBe("Local");

			// ...and the framework defaults are filtered out unless asked for.
			properties.Properties.ShouldNotContain(property => property.Provenance == "Default");

			var withDefaults = await session.ReadXamlPropertiesAsync(rootGrid.Handle, includeDefaults: true, cancellationToken);
			withDefaults.Properties.ShouldContain(property => property.Provenance == "Default");
			withDefaults.Count.ShouldBeGreaterThan(properties.Count);

			// A concrete string value comes through: the caption's Text is exactly what the XAML sets.
			var captionProperties = await session.ReadXamlPropertiesAsync(caption!.Handle, includeDefaults: false, cancellationToken);
			var text = captionProperties.Properties.FirstOrDefault(property => property.Name == "Text");
			text.ShouldNotBeNull();
			text!.Value.ShouldBe("Rose UWP Probe");
			text.Provenance.ShouldBe("Local");

			// The caption's declaration is three attributes, and exactly those three come back. The
			// UIElement composition properties -- CenterPoint, Rotation, Scale and the rest -- read as
			// BaseValueSourceLocal the moment the framework touches one, so they were reported as six
			// local sets that the markup does not make, crowding out the properties that matter.
			var composition = new[] { "CenterPoint", "Rotation", "RotationAxis", "Scale", "TransformMatrix", "Translation" };
			captionProperties.Properties.ShouldNotContain(property => composition.Contains(property.Name));

			// Still available to anyone who asks for everything on the element; just not offered as
			// evidence of what the XAML sets.
			var captionDefaults = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: true, cancellationToken);
			captionDefaults.Properties.ShouldContain(property => property.Name == "Scale");

			// No property claims a location it cannot support. XAML diagnostics reports source info per
			// source object, not per property, so for a value set on the element the only location
			// available is the element's own tag -- which the element carries, and which was being
			// copied onto every property. That made a genuine attribution byte-identical to a
			// fabricated one, so it is emitted only when the source is something other than the element.
			foreach (var property in captionProperties.Properties.Where(property => property.Provenance == "Local"))
			{
				property.SourceFile.ShouldBeNull();
			}

			// The element's own declaration is real, and is where it has always belonged.
			captionProperties.SourceFile.ShouldBe("ms-appx:///MainPage.xaml");
			// The line is read out of the markup rather than written here as a number, because a
			// number here is one that every edit to the probe above this element silently invalidates --
			// and the failure it produces names this test rather than the edit that moved the line. The
			// two sides stay independent: the tool reports what the markup compiler baked into the app,
			// and this reads the file.
			captionProperties.SourceLine.ShouldBe(DeclarationLineOf("Caption"));
		}
	}

	/// <summary>
	/// #21: a CornerRadius came back as an empty string while the Thickness beside it, set by the same
	/// markup, came back as "24,24,24,24". Not our formatting -- the framework populates that BSTR
	/// itself and populated it with nothing -- so it is read off the element instead.
	/// <para>
	/// A sweep of every property of every element in this app settled how far to go: 3,485 rows, 32 of
	/// them empty while not null, and of those the only struct type was CornerRadius. So one per-type
	/// special case rather than twenty, and a flag for whatever the next one turns out to be --
	/// <c>CornerRadiusProtected</c> is already it, being protected and absent from the projection.
	/// </para>
	/// <para>
	/// Skips where the UWP or C++ toolchain is absent.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(2)]
	public async Task Reads_a_corner_radius_the_framework_renders_as_nothing()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C: builds what it reads in a slot of its own. The values are the ones the shared Pane
		// carries, but owning the element means this cannot be disturbed by a test editing that one,
		// and reading it cannot change what such a test sees.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup,
				turn.MarkupHolding("<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />"),
				filePath: null,
				cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			var subtree = await session.ReadXamlTreeAsync(turn.Slot, offset: 0, limit: 0, cancellationToken);
			var pane = subtree.Nodes.FirstOrDefault(node => node.Address == turn.Address("Border[0]"));
			pane.ShouldNotBeNull();

			var properties = await session.ReadXamlPropertiesAsync(pane!.Handle, includeDefaults: false, cancellationToken);
			(properties.Detail is null).ShouldBeTrue($"expected properties, got detail: {properties.Detail}");

			// The markup says CornerRadius="8", and it reads back in the same four-number form the
			// Thickness beside it uses, so a caller that parses one parses the other.
			var radius = properties.Properties.FirstOrDefault(property => property.Name == "CornerRadius");
			radius.ShouldNotBeNull();
			radius!.Value.ShouldBe("8,8,8,8");
			radius.ValueUnavailable.ShouldBeFalse("a corner radius of zero is a value rather than an absence");

			var padding = properties.Properties.FirstOrDefault(property => property.Name == "Padding");
			padding.ShouldNotBeNull();
			padding!.Value.ShouldBe("24,24,24,24");

			// The other half: something the framework will not render, and cannot be read a second way
			// because it is protected and not in the projection, says so rather than looking unset.
			// An empty value that means two different things is how the CornerRadius case hid.
			// Named rather than taken as the first row: under a shared app the tree's order is not this
			// test's to rely on, and "whatever came back first" is the sort of assumption that fails
			// later for a reason nobody connects to this line.
			var whole = await session.ReadXamlTreeAsync(cancellationToken);
			var root = whole.Nodes.Single(node => node.Name == "RootGrid");
			var all = await session.ReadXamlPropertiesAsync(root.Handle, includeDefaults: true, cancellationToken);
			var unavailable = all.Properties.Where(property => property.ValueUnavailable).ToArray();

			foreach (var property in unavailable)
			{
				property.Value.ShouldBe(string.Empty);
			}

			// And a string that is genuinely empty is not flagged, or the flag fires on the majority of
			// empty values and stops meaning anything.
			all.Properties.Where(property => property.ValueType == "Windows.Foundation.String").ShouldNotContain(
				property => property.ValueUnavailable);
		}
	}

	/// <summary>
	/// The one-based line of the classic probe's markup that declares an element with this
	/// <c>x:Name</c>, so a test asserting source info does not carry a line number of its own.
	/// </summary>
	/// <param name="name">The <c>x:Name</c> of the element, which the probe gives every declared one.</param>
	private static int DeclarationLineOf(string name)
	{
		var markup = File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic", "MainPage.xaml"));
		var declaration = $"x:Name=\"{name}\"";

		for (var line = 0; line < markup.Length; line++)
		{
			if (markup[line].Contains(declaration, StringComparison.Ordinal)) return line + 1;
		}

		throw new InvalidOperationException($"The classic probe's markup declares no element named {name}.");
	}

	/// <summary>
	/// What a second read of an element's properties reports (#97). Not the behaviour anyone would
	/// choose -- it is the behaviour there is, pinned so it stops being a surprise.
	/// <para>
	/// Reading an element's property chain brings its untouched collection properties into existence,
	/// and a property that exists is no longer the framework's default. So the second read of a
	/// <c>TextBlock</c> reports <c>Inlines</c>, <c>TextHighlighters</c> and
	/// <c>SelectionHighlightColor</c> as <c>Local</c>, with provenance and values as plausible as the
	/// ones the markup really set. The first read is the accurate one, and it is our own read that
	/// spoils it -- which also means <c>rose_xaml_properties</c> is declared read-only and is not
	/// quite, though nothing the app draws changes.
	/// </para>
	/// <para>
	/// Measured before it was documented: the three extras arrive with <c>provenance=Local</c>, so
	/// there is no source to filter on and the one-line fix does not exist. A <c>Border</c> is stable
	/// across reads, so this is TextBlock's text properties rather than something general -- which is
	/// why the assertions below are about shape and not about a fixed list of names.
	/// </para>
	/// <para>
	/// One fix must not be attempted, and it is the tempting one: caching the first read's names and
	/// filtering later reads to them would hide exactly what the apply-then-read-back loop of #12
	/// exists to verify, since an applied property need not have appeared in the first read.
	/// </para>
	/// </summary>
	[Test]
	[ClassicSlot(6)]
	public async Task A_second_properties_read_reports_what_reading_the_first_created()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		// Phase C, and the awkward one. The whole assertion is about what the *first* properties read
		// of an element returns, so it needs an element nothing has read -- and there are two ways to
		// fail that, not one. Sharing the app's Caption would make it depend on running before every
		// other test that reads a TextBlock. Building its own in a slot does not work either: an
		// element created through CreateInstance and AddChild arrives with Inlines already
		// materialised, so it was never pristine to begin with. Only markup declares an element
		// nothing has touched, so it reads a declared one that belongs to it alone. The Border it
		// compares against can be built, because a Border has no collection property to materialise
		// -- which is the point that test is making.
		await using var turn = await probe.TakeSlotAsync(cancellationToken);
		var session = turn.Session;
		{
			var built = await session.ApplyXamlAsync(
				turn.EmptyMarkup,
				turn.MarkupHolding("<Border Background=\"#FF202830\" Padding=\"24\" CornerRadius=\"8\" />"),
				filePath: null,
				cancellationToken);
			(built.Detail is null).ShouldBeTrue($"expected the slot to be filled, got detail: {built.Detail}");

			var whole = await session.ReadXamlTreeAsync(cancellationToken);
			var caption = whole.Nodes.Single(node => node.Name == "PristineText");
			var pane = whole.Nodes.Single(node => node.Address == turn.Address("Border[0]"));

			// A tree read does not do it -- only a properties read of that element does -- so this
			// first read of the caption is still the markup's own answer.
			var first = await NamesOfAsync(session, caption.Handle, cancellationToken);
			first.ShouldBe(["FontSize", "Foreground", "Text"]);

			var second = await NamesOfAsync(session, caption.Handle, cancellationToken);

			// A superset, never a different set: nothing the markup set may disappear.
			foreach (var name in first)
			{
				second.ShouldContain(name);
			}
			second.Count.ShouldBeGreaterThan(first.Count, $"expected the second read to grow, got {second.Count}");

			// And the additions are indistinguishable by provenance, which is the finding that
			// decided against filtering. If this ever fails because an addition arrives as something
			// other than Local, there is a filter to write and #97 can be fixed properly.
			var properties = await session.ReadXamlPropertiesAsync(caption.Handle, includeDefaults: false, cancellationToken);
			foreach (var added in second.Except(first))
			{
				var property = properties.Properties.Single(candidate => candidate.Name == added);
				property.Provenance.ShouldBe("Local");
			}

			// A Border has no collection property to materialise, so it does not move. This is what
			// makes the behaviour a property of the element's type rather than of reading as such.
			var paneFirst = await NamesOfAsync(session, pane.Handle, cancellationToken);
			var paneSecond = await NamesOfAsync(session, pane.Handle, cancellationToken);
			paneSecond.ShouldBe(paneFirst);
		}
	}

	/// <summary>The names of one element's set properties, ordered so two reads can be compared.</summary>
	private static async Task<List<string>> NamesOfAsync(
		LiveAppSession session,
		ulong handle,
		CancellationToken cancellationToken)
	{
		var properties = await session.ReadXamlPropertiesAsync(handle, includeDefaults: false, cancellationToken);
		(properties.Detail is null).ShouldBeTrue($"expected properties, got detail: {properties.Detail}");

		return [.. properties.Properties.Select(property => property.Name).Order(StringComparer.Ordinal)];
	}
}
