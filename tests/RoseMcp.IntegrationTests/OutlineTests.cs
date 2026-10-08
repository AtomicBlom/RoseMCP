using System.Text.Json;

using ModelContextProtocol;

using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The reads that come before an edit. Answering "what is in this type" with a file read is what
/// puts the file in front of the caller, and once it is open the edit goes through a text tool --
/// which is the point at which none of the rest of this surface is worth reaching for.
/// </summary>
public sealed class OutlineTests
{
	[Test]
	public async Task Lists_a_types_members_with_their_signatures()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: true,
			includeSignatures: true,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Kind.ShouldBe("class");
		type.Namespace.ShouldBe("Library");
		type.Summary.ShouldBe("Says hello, at various lengths.");

		// The compiler's signature, so an implementer can be written from this alone.
		type.Members.ShouldContain(
			member => member.Signature == "string Library.Greeter.Greet(string name)");

		type.Members.ShouldContain(member => member.Name == "PrefixLength" && member.Kind == "Property");
		type.Members.ShouldContain(member => member.Name == "Shout" && member.Accessibility == "Private" && member.IsStatic);

		// The file is said once, on the type, and each member gives the line it is on there.
		Path.GetFileName(type.FilePath).ShouldBe("Greeter.cs");
		type.Declarations.ShouldHaveSingleItem().Project.ShouldBe("Library");
		type.TotalMembers.ShouldBe(type.Members.Count);
		result.Truncated.ShouldBeFalse();

		// A reference in a summary is the name it points at, which is usually the subject of the
		// sentence, rather than a hole where that name was.
		type.Members.Single(member => member.Signature == "string Library.Greeter.Greet(string title, string name)")
			.Summary.ShouldBe("The greeting for name with a title, its prefix PrefixLength characters long.");

		foreach (var member in type.Members)
		{
			member.Line.ShouldNotBeNull();
			member.FilePath.ShouldBeNull($"{member.Name} is in its type's own file");
			member.DeclaringType.ShouldBeNull($"{member.Name} is the type's own");
		}

		type.Members.Where(member => member.Name == "PrefixLength").ShouldHaveSingleItem().Line.ShouldBe(9);
	}

	/// <summary>
	/// The names alone, for the case the outline is worst at: a large type, where the signatures and
	/// the documentation are most of the answer and neither is what the caller is looking for. Two
	/// switches rather than one, because a caller who wants to know what a member is for and one who
	/// wants to know what it takes are asking different questions.
	/// </summary>
	[Test]
	public async Task Leaves_out_the_documentation_and_signatures_when_asked_to()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		// What is left is what a search through a large type needs: the names, and where they are.
		type.Summary.ShouldBeNull();
		foreach (var member in type.Members)
		{
			member.Signature.ShouldBeNull();
			member.Summary.ShouldBeNull();
			member.Kind.ShouldNotBeEmpty();
			member.Line.ShouldNotBeNull();
		}
		type.Members.ShouldContain(member => member.Name == "Greet");
	}

	/// <summary>
	/// The two switches are independent, so asking for one does not silently bring the other. Both
	/// default to off at the tool, which <c>OutlineToolTests</c> in the unit suite holds.
	/// </summary>
	[Test]
	[Arguments(true, false)]
	[Arguments(false, true)]
	public async Task Answers_each_detail_switch_on_its_own(bool documentation, bool signatures)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: documentation,
			includeSignatures: signatures,
			TestContext.Current!.Execution.CancellationToken);

		// PrefixLength rather than Greet: it is declared once, so the name identifies it whether or not
		// the signature that would otherwise tell the overloads apart is in the answer.
		var member = result.Types.ShouldHaveSingleItem().Members.Where(
			candidate => candidate.Name == "PrefixLength").ShouldHaveSingleItem();

		(member.Signature is not null).ShouldBe(signatures);
		(member.Summary is not null).ShouldBe(documentation);
	}

	/// <summary>
	/// An interface outline is what implementing it needs: the members are abstract and the
	/// signatures are complete.
	/// </summary>
	[Test]
	public async Task Marks_the_members_an_implementer_has_to_write()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.IShape",
			filePath: null,
			includeInherited: false,
			includeDocumentation: true,
			includeSignatures: true,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Kind.ShouldBe("interface");

		var member = type.Members.ShouldHaveSingleItem();

		member.IsAbstract.ShouldBeTrue();
		member.Signature.ShouldBe("double Library.IShape.Area()");
	}

	/// <summary>
	/// A file path is the other root, and the types come back in the order the file writes them
	/// rather than sorted -- sorting would make the outline and the file disagree.
	/// </summary>
	[Test]
	public async Task Lists_every_type_in_a_file_in_the_order_it_declares_them()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Kinds.cs"),
			includeInherited: false,
			includeDocumentation: true,
			includeSignatures: true,
			TestContext.Current!.Execution.CancellationToken);

		result.Types.Select(type => type.Name).ShouldBe(["Library.IShape", "Library.Colour", "Library.Empty"]);
		result.Types.Select(type => type.Kind).ShouldBe(["interface", "enum", "class"]);
	}

	/// <summary>Naming both, or neither, is a choice the caller has to make rather than one to guess at.</summary>
	[Test]
	[Arguments("Library.Greeter", "Greeter.cs")]
	[Arguments(null, null)]
	public async Task Refuses_both_roots_or_none(string? type, string? file)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var path = file is null ? null : fixture.Path("Members", "Library", file);

		await Should.ThrowAsync<ArgumentException>(
			() => OutlineService.OutlineAsync(
				snapshot,
				type,
				path,
				includeInherited: false,
				includeDocumentation: true,
				includeSignatures: true,
				TestContext.Current!.Execution.CancellationToken)).OfExactType();
	}

	/// <summary>
	/// What every member of a type shares is said once, on the type, and a flag a member does not have
	/// is left out rather than written as false: on the wire, a member is its name, kind, accessibility
	/// and line. Measured over the result as the MCP layer serialises it, because that is what a
	/// caller pays for, and a shape that is lean only in C# is not lean.
	/// </summary>
	[Test]
	public async Task Says_what_every_member_shares_once_on_the_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var json = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)!;
		var type = json["types"]![0]!;

		Path.GetFileName(type["filePath"]!.GetValue<string>()).ShouldBe("Greeter.cs");
		type["declarations"]![0]!["project"]!.GetValue<string>().ShouldBe("Library");
		type["declarations"]![0]!["containingMember"].ShouldBeNull("a type's declaration sits inside the type itself");

		string[] perFile = ["filePath", "project", "isTestProject", "preview", "column", "containingMember", "location"];
		string[] falseFlags = ["isAbstract", "isStatic", "isGenerated"];

		foreach (var member in type["members"]!.AsArray())
		{
			var keys = member!.AsObject().Select(property => property.Key).ToList();

			keys.ShouldContain("line");
			keys.ShouldNotContain(key => perFile.Contains(key), $"{member["name"]} repeats a field its type already says: {member.ToJsonString()}");

			foreach (var flag in falseFlags)
			{
				if (member[flag] is { } value) value.GetValue<bool>().ShouldBeTrue($"{member["name"]} writes {flag} as false");
			}
		}

		// Shout is the one static member, so the flag is there for it and absent for the rest.
		type["members"]!.AsArray().Count(member => member!["isStatic"] is not null).ShouldBe(1);
	}

	/// <summary>
	/// The way into a large type: a name filter, matched anywhere in the name and ignoring case, with
	/// the total saying how many matched.
	/// </summary>
	[Test]
	public async Task Lists_only_the_members_whose_name_matches()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken,
			members: "GREET");

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.Select(member => member.Name).ShouldBe(["Greet", "Greet"]);
		type.TotalMembers.ShouldBe(2);
		result.Truncated.ShouldBeFalse();
		result.Notices.ShouldBeEmpty();
	}

	/// <summary>
	/// A filter matching nothing is said, rather than answered with an empty list that reads exactly
	/// like a type with no members.
	/// </summary>
	[Test]
	public async Task Says_so_when_the_name_filter_matches_nothing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken,
			members: "Farewell");

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.ShouldBeEmpty();
		type.TotalMembers.ShouldBe(0);
		result.Notices.ShouldContain(notice => notice.Contains("No member's name contains 'Farewell'") && notice.Contains("all 6 members"));
	}

	/// <summary>
	/// The cap stops the listing and says so, with the total, so a caller knows the answer is partial
	/// and how much is missing rather than taking the first page for the type.
	/// </summary>
	[Test]
	public async Task Stops_at_the_member_cap_and_says_how_many_there_were()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Greeter",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken,
			maxMembers: 2);

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.Count.ShouldBe(2);
		type.TotalMembers.ShouldBe(6);
		result.Truncated.ShouldBeTrue();
		result.Notices.ShouldContain(notice => notice.Contains("Listed 2 of 6 members") && notice.Contains("maxMembers=2"));
	}

	/// <summary>
	/// One cap for the whole answer, so a file of many types cannot overrun it a type at a time; the
	/// types it ran out before are still listed, with the totals that say what they hold.
	/// </summary>
	[Test]
	public async Task Shares_the_member_cap_across_every_type_in_a_file()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Kinds.cs"),
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken,
			maxMembers: 1);

		result.Types.Select(type => type.Members.Count).ShouldBe([1, 0, 0]);
		result.Types.Select(type => type.TotalMembers).ShouldBe([1, 2, 0]);
		result.Truncated.ShouldBeTrue();
	}

	/// <summary>
	/// A member declared outside its type's file names that file, and only that member does: the other
	/// part of a partial gives its own path, and the part being looked at gives a line alone.
	/// </summary>
	[Test]
	public async Task Names_the_file_only_for_a_member_declared_in_another()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Split",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Declarations.Count.ShouldBe(2);
		type.DeclaredElsewhere.ShouldBe(0);

		var home = Path.GetFileName(type.FilePath);
		var away = home == "Split.cs" ? "SplitAgain.cs" : "Split.cs";
		var listed = type.Members.ToDictionary(member => member.Name);

		listed.Keys.ShouldBe(["First", "Second"], ignoreOrder: true);
		listed.Values.Where(member => member.FilePath is null).ShouldHaveSingleItem();
		listed.Values.Where(member => member.FilePath is not null).ShouldHaveSingleItem().FilePath.ShouldEndWith(away);
	}

	/// <summary>
	/// A file outline lists what the file declares. A partial's other files are named among its
	/// declarations and their members counted, so the short list does not read as the whole type, and
	/// the notice says how to ask for all of it.
	/// </summary>
	[Test]
	public async Task Lists_only_what_a_file_declares_and_counts_the_rest_of_a_partial()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Split.cs"),
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.ShouldHaveSingleItem().Name.ShouldBe("First");
		type.TotalMembers.ShouldBe(1);
		type.DeclaredElsewhere.ShouldBe(1);
		type.Declarations.Count.ShouldBe(2);
		result.Notices.ShouldContain(notice => notice.Contains("1 member of Library.Split declared in its other files is not listed"));
	}

	/// <summary>
	/// The case that makes it matter: a XAML code-behind's type is mostly its generated half, whose
	/// members are none of what a caller outlining the code-behind is about to edit.
	/// </summary>
	[Test]
	public async Task Leaves_a_code_behinds_generated_half_out_of_its_file_outline()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("XamlStub", "Ui", "Widget.xaml.cs"),
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.Select(member => member.Name).ShouldBe([".ctor", "Caption"], ignoreOrder: true);
		type.Members.ShouldAllBe(member => member.FilePath == null && !member.IsGenerated);
		type.DeclaredElsewhere.ShouldBeGreaterThan(0, "the generated half declares InitializeComponent and the named element");

		var whole = await OutlineService.OutlineAsync(
			snapshot,
			"Ui.Widget",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		whole.Types.ShouldHaveSingleItem().Members.ShouldContain(member => member.Name == "InitializeComponent");
	}

	/// <summary>
	/// A partial type written as two blocks of one file is one type in the outline. Listed once per
	/// block, it would spend the member cap twice and its totals would count every member twice.
	/// </summary>
	[Test]
	public async Task Lists_a_type_split_within_one_file_once()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Halved.cs"),
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Members.Select(member => member.Name).ShouldBe(["FrontHalf", "BackHalf"]);
		type.TotalMembers.ShouldBe(2);
		type.Members.ShouldAllBe(member => member.FilePath == null);
		result.Truncated.ShouldBeFalse();
	}

	/// <summary>
	/// An inherited member says which type declared it, which is the one case where the type it is
	/// listed under is not its own.
	/// </summary>
	[Test]
	public async Task Says_which_base_an_inherited_member_comes_from()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.LoudNotifier",
			filePath: null,
			includeInherited: true,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var members = result.Types.ShouldHaveSingleItem().Members.Where(member => member.Name == "Notify").ToList();

		members.Count.ShouldBe(2);
		members[0].DeclaringType.ShouldBeNull("the override is LoudNotifier's own");
		members[1].DeclaringType.ShouldBe("Library.Notifier");
	}

	/// <summary>
	/// A primary constructor is documented by its type's comment, so with documentation on, its entry
	/// would repeat the type's summary -- the longest paragraph in most answers, paid twice.
	/// </summary>
	[Test]
	public async Task Does_not_repeat_the_types_summary_on_its_primary_constructor()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Composed",
			filePath: null,
			includeInherited: false,
			includeDocumentation: true,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Summary.ShouldNotBeNull();
		type.Members.Where(member => member.Name == ".ctor").ShouldHaveSingleItem().Summary.ShouldBeNull();
	}

	/// <summary>
	/// A summary in an outline is its first sentence, as the argument promises: a type documented in two
	/// paragraphs costs one sentence, and the rest is a rose_symbol_info call away.
	/// </summary>
	[Test]
	public async Task Gives_each_summary_as_its_first_sentence()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			"Library.Literal",
			filePath: null,
			includeInherited: false,
			includeDocumentation: true,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		result.Types.ShouldHaveSingleItem().Summary.ShouldBe(
			"A multi-line raw string literal already on disk, indented more deeply than any write path would "
				+ "place it, so every write to this file moves it and none of them can pass by leaving it alone.");
	}

	/// <summary>
	/// What the projects can see, and what a change to one of them reaches. The transitive half is
	/// the point: the set that breaks is everything depending on the project, not everything naming
	/// the member.
	/// </summary>
	[Test]
	public async Task Reports_which_projects_depend_on_which()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = ProjectGraphService.Describe(snapshot, project: null);

		var core = result.Projects.Where(project => project.Name == "Core").ShouldHaveSingleItem();
		var app = result.Projects.Where(project => project.Name == "App").ShouldHaveSingleItem();

		core.ReferencedBy.ShouldBe(["App"]);
		core.References.ShouldBeEmpty();
		app.References.ShouldBe(["Core"]);
		core.IsTestProject.ShouldBeFalse("the library is not a test project");
		core.DocumentCount.ShouldBeGreaterThan(0);
	}

	[Test]
	public async Task Refuses_a_project_that_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var thrown = Should.Throw<ArgumentException>(() => ProjectGraphService.Describe(snapshot, "Nowhere")).ShouldBeOfType<ArgumentException>();

		thrown.Message.ShouldContain("It has App, Core.", Case.Sensitive);
	}

	/// <summary>
	/// A reference says which member it is inside, which is what turns a flat list of call sites into
	/// the answer to the question that was actually asked.
	/// </summary>
	[Test]
	public async Task Says_which_member_a_reference_is_inside()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		var reference = references.Listed().ShouldHaveSingleItem();

		reference.Site.ContainingMember.ShouldBe("Caller.Call");
		reference.File.Project.ShouldBe("Library");
		reference.File.IsTestProject.ShouldBeFalse("the reference is in the library rather than a test project");
	}

	/// <summary>A type's own bases and interfaces, which were reported only for members.</summary>
	[Test]
	public async Task Reports_what_a_type_derives_from()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Shapes.Circle" },
			TestContext.Current!.Execution.CancellationToken);

		info.BaseDefinitions.ShouldContain(super => super.Name == "IShape");
	}
}
