using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The XAML view's half of a live edit: the tree narrowed by a search, by the app's own markup and by what changed;
/// changed elements marked, and each changed property showing what it was, what it became and who changed it --
/// including while the target is stopped, when only the record can be read.
/// </summary>
public sealed class XamlChangeViewTests
{
	private static readonly DateTime When = new(2026, 10, 12, 3, 5, 0, DateTimeKind.Utc);

	/// <summary>A window, its page, an ink toolbar of the app's own, and a template part inside its slider.</summary>
	private static XamlInspection Inspection()
	{
		var inspection = new XamlInspection();

		inspection.Absorb(new LiveXamlTree
		{
			Nodes =
			[
				Node(1, 0, "Windows.UI.Xaml.Controls.Frame", file: null),
				Node(2, 1, "Drawboard.DocumentPage", file: "ms-appx:///Views/DocumentPage.xaml"),
				Node(3, 2, "Windows.UI.Xaml.Controls.StackPanel", name: "ToolStrip", file: "ms-appx:///Views/InkToolbar.xaml"),
				Node(4, 3, "Windows.UI.Xaml.Controls.Slider", name: "WidthSlider", file: "ms-appx:///Views/InkToolbar.xaml"),
				Node(5, 4, "Windows.UI.Xaml.Controls.Grid", file: "ms-resource:///Files/windows.ui.xaml;component/themes/generic.xaml"),
				Node(6, 3, "Windows.UI.Xaml.Controls.TextBlock", name: "WidthLabel", file: "ms-appx:///Views/InkToolbar.xaml"),
			],
		});

		return inspection;
	}

	private static LiveXamlNode Node(ulong handle, ulong parent, string typeName, string? name = null, string? file = null) =>
		new() { Handle = handle, Parent = parent, ChildIndex = (int)handle, TypeName = typeName, Name = name, File = file, Address = name is null ? null : $"#{name}" };

	private static LiveXamlChange Change(long sequence, ulong handle, string property, string? old, string? now, string kind = "SetProperty") => new()
	{
		Sequence = sequence,
		ChangedUtc = When,
		Person = false,
		Agent = new LiveAppAgent { ClientId = "c-1", Name = "D:\\Drawboard\\PDF", Connected = true },
		Tool = ToolNames.XamlApply,
		Kind = kind,
		Target = "#ToolStrip",
		Handle = handle,
		Property = property,
		OldValue = old,
		NewValue = now,
	};

	private static LiveXamlChangeList Changes(params LiveXamlChange[] changes) =>
		new() { SessionId = "s", Changes = changes, Version = "v" };

	private static IEnumerable<XamlNodeRow> Shown(XamlInspection inspection) =>
		Walk(inspection.Roots).Where(row => row.IsShown);

	private static IEnumerable<XamlNodeRow> Walk(IEnumerable<XamlNodeRow> rows) =>
		rows.SelectMany(row => Walk(row.Children).Prepend(row));

	/// <summary>
	/// A search keeps what matches and the way down to it, and opens that way, since a match inside a closed row is one
	/// nobody can see; clearing it puts the whole tree back.
	/// </summary>
	[Test]
	public void A_search_keeps_its_matches_and_the_way_down_to_them()
	{
		var inspection = Inspection();
		inspection.Absorb(new LiveXamlSelection { JustMyXaml = false });
		inspection.Row(3)!.IsExpanded = false;

		inspection.FindText = "widthlabel";

		Shown(inspection).Select(row => row.Handle).Order().ShouldBe([1UL, 2UL, 3UL, 6UL]);
		inspection.Row(3)!.IsExpanded.ShouldBeTrue();
		inspection.FilterNote.ShouldBe("1 element of 6 match.");

		inspection.FindText = string.Empty;
		Shown(inspection).Count().ShouldBe(6);
		inspection.FilterNote.ShouldBe(string.Empty);
	}

	/// <summary>A search reads the type, the name, the address and the file, without regard to case.</summary>
	[Test]
	[Arguments("SLIDER", 4UL)]
	[Arguments("#WidthSlider", 4UL)]
	[Arguments("DocumentPage.xaml", 2UL)]
	public void A_search_reads_the_type_the_name_the_address_and_the_file(string text, ulong expected)
	{
		var inspection = Inspection();

		inspection.FindText = text;

		inspection.Row(expected)!.IsShown.ShouldBeTrue();
		inspection.Row(5)!.IsShown.ShouldBeFalse();
	}

	/// <summary>
	/// Just my XAML leaves out a control template's parts and keeps everything the app's markup declares, with the way
	/// down to it -- and is the app's own toggle, so it moves when the app says it moved.
	/// </summary>
	[Test]
	public void Just_my_xaml_leaves_out_a_templates_parts()
	{
		var inspection = Inspection();

		inspection.Absorb(new LiveXamlSelection { JustMyXaml = false });
		Shown(inspection).Count().ShouldBe(6);

		inspection.Absorb(new LiveXamlSelection { JustMyXaml = true });

		inspection.Row(5)!.IsShown.ShouldBeFalse("a template's grid is the framework's markup");
		inspection.Row(1)!.IsShown.ShouldBeTrue("the frame has no source of its own and is the way down to the app's page");
		inspection.Row(4)!.IsShown.ShouldBeTrue();
	}

	/// <summary>
	/// A tree with no source info at all cannot tell the app's markup from a template's, so Just my XAML leaves it
	/// whole and says why, rather than emptying it into what reads as an app with no UI.
	/// </summary>
	[Test]
	public void Just_my_xaml_on_a_tree_with_no_source_info_leaves_it_whole_and_says_so()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(new LiveXamlTree { Nodes = [Node(1, 0, "Grid"), Node(2, 1, "Border")] });

		inspection.JustMyXaml.ShouldBeTrue();
		Shown(inspection).Count().ShouldBe(2);
		inspection.FilterNote.ShouldContain("no source info");
	}

	/// <summary>The same test of whose markup an element is as the in-app toolbar's, so the tree hides nothing a click there selects.</summary>
	[Test]
	[Arguments("ms-appx:///Views/Shell/ShellView.xaml", true)]
	[Arguments("ms-appx://Microsoft.UI.Xaml.2.8/Microsoft.UI.Xaml/Themes/21h1_themeresources.xaml", false)]
	[Arguments("ms-resource:///Files/Themes/Default/Controls/TextBox.Styles.xaml", true)]
	[Arguments("ms-resource:///Files/windows.ui.xaml;component/themes/generic.xaml", false)]
	[Arguments("", false)]
	[Arguments(null, false)]
	public void Whose_markup_an_element_is_follows_the_toolbars_rule(string? file, bool apps)
	{
		XamlTreeFilter.IsAppMarkup(file).ShouldBe(apps);
	}

	/// <summary>
	/// Each element a live edit changed is marked, with what changed in its tooltip, and the Changed filter shows those
	/// and the way down to them; a filter showing nothing says what that means.
	/// </summary>
	[Test]
	public void Changed_elements_are_marked_and_the_changed_filter_shows_only_them()
	{
		var inspection = Inspection();
		inspection.ChangedOnly = true;
		inspection.FilterNote.ShouldBe("No element in this tree has been changed by a live edit this session.");

		inspection.Absorb(Changes(Change(1, 3, "Margin", "12,0,12,0", "8,0,8,0")));

		inspection.ChangedCount.ShouldBe(1);
		inspection.Row(3)!.IsChanged.ShouldBeTrue();
		inspection.Row(3)!.ChangeNote.ShouldContain("Margin 12,0,12,0 \u2192 8,0,8,0");
		inspection.Row(3)!.ChangeNote.ShouldContain("D:\\Drawboard\\PDF");
		Shown(inspection).Select(row => row.Handle).Order().ShouldBe([1UL, 2UL, 3UL]);
	}

	/// <summary>
	/// The element on screen shows each changed property first, what it was and became and who changed it; a property
	/// the read did not list -- one cleared back to its default -- is still there, from the record.
	/// </summary>
	[Test]
	public void A_changed_property_shows_its_old_and_new_value_and_who_changed_it()
	{
		var inspection = Inspection();
		inspection.Select(inspection.Row(3)!);

		inspection.Absorb(new LiveXamlProperties
		{
			Handle = 3,
			Properties =
			[
				new LiveXamlProperty { Name = "Orientation", Value = "Horizontal", Provenance = "Local" },
				new LiveXamlProperty { Name = "Margin", Value = "8,0,8,0", Provenance = "Local" },
			],
		});

		inspection.Absorb(Changes(
			Change(1, 3, "Margin", "4,0,4,0", "12,0,12,0"),
			Change(2, 3, "Margin", "12,0,12,0", "8,0,8,0"),
			Change(3, 3, "Padding", "6", null, kind: "ClearProperty")));

		var rows = inspection.Properties;
		rows.Count.ShouldBe(3);
		rows[0].Name.ShouldBe("Margin");
		rows[0].ChangedValues.ShouldBe("12,0,12,0 \u2192 8,0,8,0", "the newest change of a property is the one shown");
		rows[0].ChangedBy.ShouldStartWith("D:\\Drawboard\\PDF  \u00B7  rose_xaml_apply  \u00B7  ");
		rows[1].Name.ShouldBe("Padding");
		rows[1].Value.ShouldBe(XamlChangeText.Cleared);
		rows[1].ChangedValues.ShouldBe("6 \u2192 (cleared)");
		rows[2].Name.ShouldBe("Orientation");
		rows[2].HasChange.ShouldBeFalse();
	}

	/// <summary>
	/// A stopped target cannot be read, but what changed on an element is the broker's record and still true, so it is
	/// what the element shows, with the reason the rest is not there.
	/// </summary>
	[Test]
	public void A_stopped_targets_element_shows_what_changed_on_it()
	{
		var inspection = Inspection();
		inspection.Absorb(Changes(Change(1, 3, "Margin", "12,0,12,0", "8,0,8,0")));
		inspection.Select(inspection.Row(3)!);

		inspection.ShowChangesOnly(InspectorText.PropertiesNeedARunningTarget);

		inspection.Properties.ShouldHaveSingleItem().ChangedValues.ShouldBe("12,0,12,0 \u2192 8,0,8,0");
		inspection.PropertiesDetail.ShouldBe(InspectorText.PropertiesNeedARunningTarget);
	}

	/// <summary>A stopped target's tree is the last one read, and says when that was.</summary>
	[Test]
	public void A_stopped_targets_tree_says_when_it_was_read()
	{
		var inspection = new XamlInspection();
		inspection.Absorb(new LiveXamlTree { Nodes = [Node(1, 0, "Grid")], AsOfUtc = When, Detail = "The target is stopped." });

		inspection.AsOfUtc.ShouldBe(When);
		inspection.Count.ShouldBe(1);
		InspectorText.TreeAsOf(When).ShouldContain(When.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// Asking about an element names it by its handle, which is what an agent's XAML tools take, and says the change
	/// that is likely the question.
	/// </summary>
	[Test]
	public void Asking_about_an_element_names_its_handle_and_its_latest_change()
	{
		var inspection = Inspection();
		var changes = new[] { Change(1, 3, "Margin", "12,0,12,0", "8,0,8,0") };

		var subject = AskAgent.Element("session-1", inspection.Row(3)!, changes);

		subject.Kind.ShouldBe(NoteSubjectKind.Element);
		subject.Session.ShouldBe("session-1");
		subject.Element.ShouldBe("3");
		subject.Readable.ShouldContain("StackPanel #ToolStrip");
		subject.Readable.ShouldContain("Margin 12,0,12,0 \u2192 8,0,8,0");
	}

	/// <summary>A change the agent session made is said with who, with what and when; the person's, and a gone agent's, likewise.</summary>
	[Test]
	public void Who_changed_it_is_said_by_the_client_table_or_as_gone()
	{
		var change = Change(1, 3, "Margin", "12", "8");

		XamlChangeText.By(change).ShouldStartWith("D:\\Drawboard\\PDF");
		XamlChangeText.By(change with { Agent = null }).ShouldStartWith("an agent session no longer connected");
		XamlChangeText.By(change with { Agent = null, Person = true }).ShouldStartWith("you");
		XamlChangeText.Values(change with { OldValue = null }).ShouldBe($"{XamlChangeText.Unread} \u2192 8");
		XamlChangeText.Values(change with { OldValue = string.Empty }).ShouldBe("\"\" \u2192 8");
	}

	/// <summary>
	/// The "mark changes in the app" toggle follows what the app says, and a read that did not reach the app says
	/// nothing about it: an unknown switch leaves the toggle where it was rather than turning it on.
	/// </summary>
	[Test]
	public void A_read_that_did_not_reach_the_app_leaves_the_change_marks_toggle_alone()
	{
		var inspection = Inspection();

		inspection.Absorb(new LiveXamlSelection { ChangeMarks = false });
		inspection.ChangeMarks.ShouldBeFalse();

		inspection.Absorb(new LiveXamlSelection { Detail = "The XAML provider did not answer in time." });
		inspection.ChangeMarks.ShouldBeFalse("a switch nobody read is not on");

		inspection.Absorb(new LiveXamlSelection { ChangeMarks = true });
		inspection.ChangeMarks.ShouldBeTrue();
	}
}
