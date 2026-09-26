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
		type.Members.ShouldContain(member => member.Name == "Shout" && member.Accessibility == "Private");

		// Each member says where it is, so the next call names a file without searching.
		foreach (var member in type.Members)
		{
			member.Location.ShouldNotBeNull();
		}
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
		}
		foreach (var member in type.Members)
		{
			member.Summary.ShouldBeNull();
		}
		type.Members.ShouldContain(member => member.Name == "Greet");
		foreach (var member in type.Members)
		{
			member.Kind.ShouldNotBeEmpty();
		}
		type.Members.Any(member => member.Location is not null).ShouldBeTrue();
	}

	/// <summary>
	/// The two switches are independent, so asking for one does not silently bring the other. Both
	/// default to on, which is what every existing caller gets.
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

		thrown.Message.ShouldContain("The solution has App, Core", Case.Sensitive);
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

		var reference = references.References.ShouldHaveSingleItem();

		reference.ContainingMember.ShouldBe("Call");
		reference.Project.ShouldBe("Library");
		reference.IsTestProject.ShouldBeFalse("the reference is in the library rather than a test project");
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
