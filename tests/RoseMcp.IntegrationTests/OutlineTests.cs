namespace RoseMcp.IntegrationTests;

/// <summary>
/// The reads that come before an edit. Answering "what is in this type" with a file read is what
/// puts the file in front of the caller, and once it is open the edit goes through a text tool --
/// which is the point at which none of the rest of this surface is worth reaching for.
/// </summary>
public sealed class OutlineTests
{
	[Fact]
	public async Task Lists_a_types_members_with_their_signatures()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot, "Library.Greeter", filePath: null, includeInherited: false, TestContext.Current.CancellationToken);

		var type = Assert.Single(result.Types);

		Assert.Equal("class", type.Kind);
		Assert.Equal("Library", type.Namespace);
		Assert.Equal("Says hello, at various lengths.", type.Summary);

		// The compiler's signature, so an implementer can be written from this alone.
		Assert.Contains(
			type.Members,
			member => member.Signature == "string Library.Greeter.Greet(string name)");

		Assert.Contains(type.Members, member => member.Name == "PrefixLength" && member.Kind == "Property");
		Assert.Contains(type.Members, member => member.Name == "Shout" && member.Accessibility == "Private");

		// Each member says where it is, so the next call names a file without searching.
		Assert.All(type.Members, member => Assert.NotNull(member.Location));
	}

	/// <summary>
	/// An interface outline is what implementing it needs: the members are abstract and the
	/// signatures are complete.
	/// </summary>
	[Fact]
	public async Task Marks_the_members_an_implementer_has_to_write()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot, "Library.IShape", filePath: null, includeInherited: false, TestContext.Current.CancellationToken);

		var type = Assert.Single(result.Types);

		Assert.Equal("interface", type.Kind);

		var member = Assert.Single(type.Members);

		Assert.True(member.IsAbstract);
		Assert.Equal("double Library.IShape.Area()", member.Signature);
	}

	/// <summary>
	/// A file path is the other root, and the types come back in the order the file writes them
	/// rather than sorted -- sorting would make the outline and the file disagree.
	/// </summary>
	[Fact]
	public async Task Lists_every_type_in_a_file_in_the_order_it_declares_them()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = await OutlineService.OutlineAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Kinds.cs"),
			includeInherited: false,
			TestContext.Current.CancellationToken);

		Assert.Equal(["Library.IShape", "Library.Colour", "Library.Empty"], result.Types.Select(type => type.Name));
		Assert.Equal(["interface", "enum", "class"], result.Types.Select(type => type.Kind));
	}

	/// <summary>Naming both, or neither, is a choice the caller has to make rather than one to guess at.</summary>
	[Theory]
	[InlineData("Library.Greeter", "Greeter.cs")]
	[InlineData(null, null)]
	public async Task Refuses_both_roots_or_none(string? type, string? file)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var path = file is null ? null : fixture.Path("Members", "Library", file);

		await Assert.ThrowsAsync<ArgumentException>(
			() => OutlineService.OutlineAsync(
				snapshot, type, path, includeInherited: false, TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// What the projects can see, and what a change to one of them reaches. The transitive half is
	/// the point: the set that breaks is everything depending on the project, not everything naming
	/// the member.
	/// </summary>
	[Fact]
	public async Task Reports_which_projects_depend_on_which()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = ProjectGraphService.Describe(snapshot, project: null);

		var core = Assert.Single(result.Projects, project => project.Name == "Core");
		var app = Assert.Single(result.Projects, project => project.Name == "App");

		Assert.Equal(["App"], core.ReferencedBy);
		Assert.Empty(core.References);
		Assert.Equal(["Core"], app.References);
		Assert.False(core.IsTestProject);
		Assert.True(core.DocumentCount > 0);
	}

	[Fact]
	public async Task Refuses_a_project_that_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var thrown = Assert.Throws<ArgumentException>(() => ProjectGraphService.Describe(snapshot, "Nowhere"));

		Assert.Contains("The solution has App, Core", thrown.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A reference says which member it is inside, which is what turns a flat list of call sites into
	/// the answer to the question that was actually asked.
	/// </summary>
	[Fact]
	public async Task Says_which_member_a_reference_is_inside()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			200,
			TestContext.Current.CancellationToken);

		var reference = Assert.Single(references.References);

		Assert.Equal("Call", reference.ContainingMember);
		Assert.Equal("Library", reference.Project);
		Assert.False(reference.IsTestProject);
	}

	/// <summary>A type's own bases and interfaces, which were reported only for members.</summary>
	[Fact]
	public async Task Reports_what_a_type_derives_from()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Shapes.Circle" },
			TestContext.Current.CancellationToken);

		Assert.Contains(info.BaseDefinitions, super => super.Name == "IShape");
	}
}
