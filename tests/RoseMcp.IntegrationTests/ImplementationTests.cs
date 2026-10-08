using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Walking the hierarchy in both directions. Neither question can be answered by searching text: an
/// implementation need not mention the interface anywhere near the member, and an override's
/// documentation usually lives on the base it is hiding.
/// </summary>
public sealed class ImplementationTests
{
	[Test]
	public async Task Finds_the_types_that_implement_an_interface()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "IShape");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, new SymbolTarget { FilePath = path, Line = line, Column = column }, 200, TestContext.Current!.Execution.CancellationToken);

		result.Relationship.ShouldContain("implementing", Case.Sensitive);
		result.Matches.Select(match => match.Name).ShouldContain("Circle");
		result.Matches.Select(match => match.Name).ShouldContain("Square");

		// And they come back with somewhere to go, not just a name.
		foreach (var match in result.Matches)
		{
			match.Location.ShouldNotBeNull();
		}
	}

	[Test]
	public async Task Finds_the_members_that_implement_an_interface_member()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Area();");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, new SymbolTarget { FilePath = path, Line = line, Column = column }, 200, TestContext.Current!.Execution.CancellationToken);

		result.Matches.Count.ShouldBe(2);
		foreach (var match in result.Matches)
		{
			match.Name.ShouldBe("Area");
		}
		result.Matches.ShouldContain(match => match.Signature.Contains("Circle", StringComparison.Ordinal));
		result.Matches.ShouldContain(match => match.Signature.Contains("Square", StringComparison.Ordinal));
	}

	/// <summary>
	/// What in this solution derives from a type in a referenced assembly is a question about source
	/// asked of a symbol no project here declares, and it is the ordinary shape of the question rather
	/// than an edge of it. The base names an assembly and the answers name files.
	/// </summary>
	[Test]
	public async Task Finds_what_derives_from_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindImplementationsAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Object" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Relationship.ShouldContain("derived", Case.Sensitive);
		result.Matches.Select(match => match.Name).ShouldContain("Circle");
		result.Matches.Select(match => match.Name).ShouldContain("Square");
	}

	/// <summary>
	/// A class asks a different question from an interface, and the answer says which one it answered
	/// -- so a caller who pointed at the wrong thing can tell, rather than reading an empty list as
	/// "nothing implements this".
	/// </summary>
	[Test]
	public async Task Says_which_question_it_answered_for_a_class()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Square(double side)");

		var result = await NavigationService.FindImplementationsAsync(
			snapshot, new SymbolTarget { FilePath = path, Line = line, Column = column }, 200, TestContext.Current!.Execution.CancellationToken);

		result.Relationship.ShouldContain("derived", Case.Sensitive);
		result.Matches.ShouldBeEmpty();
	}

	[Test]
	public async Task Reports_what_a_member_implements()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var path = fixture.Path("MultiType", "Shapes", "Shapes.cs");
		var (line, column) = At(path, "Area() => Math.PI");

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { FilePath = path, Line = line, Column = column },
			TestContext.Current!.Execution.CancellationToken);

		var implemented = info.BaseDefinitions.ShouldHaveSingleItem();

		implemented.Name.ShouldBe("Area");
		implemented.Signature.ShouldContain("IShape", Case.Sensitive);
	}

	/// <summary>
	/// One-based line and column of the first occurrence of <paramref name="needle"/>, computed rather
	/// than hardcoded so editing the fixture cannot silently move what these tests point at.
	/// </summary>
	private static (int Line, int Column) At(string path, string needle)
	{
		var text = File.ReadAllText(path);
		var index = text.IndexOf(needle, StringComparison.Ordinal);

		index.ShouldBeGreaterThanOrEqualTo(0, $"'{needle}' is not in {Path.GetFileName(path)}");

		var before = text[..index];
		var lastBreak = before.LastIndexOf('\n');

		return (before.Count(character => character == '\n') + 1, index - lastBreak);
	}

	/// <summary>
	/// The same search by name. Pointing at a type meant finding a position for it first, which is a
	/// rose_search_symbols call before the question can even be asked -- two calls where a name is one.
	/// </summary>
	[Test]
	public async Task Finds_implementations_by_name()
	{
		using var fixture = FixtureSolution.Copy("MultiType", "MultiType.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindImplementationsAsync(
			snapshot,
			new SymbolTarget { Symbol = "Shapes.IShape" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Relationship.ShouldContain("implementing", Case.Sensitive);
		result.Matches.Select(match => match.Name).ShouldContain("Circle");
	}

	/// <summary>
	/// What in this solution implements a framework interface is the only form that question takes,
	/// and the referenced assemblies hold far more implementations of it than any solution does. They
	/// are counted rather than listed, and a declaration a multi-targeted project compiles once per
	/// framework is listed once.
	/// </summary>
	[Test]
	public async Task Lists_only_this_solutions_implementations_of_an_interface_from_metadata()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindImplementationsAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.IDisposable" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Matches.Select(match => match.Name).ShouldBe(["FileStore", "MemoryStore"], ignoreOrder: true);
		result.TotalCount.ShouldBe(2);
		result.Truncated.ShouldBeFalse();
		foreach (var match in result.Matches)
		{
			match.Location.ShouldNotBeNull();
		}

		result.Notices.ShouldContain(notice => notice.Contains("referenced assemblies", StringComparison.Ordinal));
	}

	/// <summary>
	/// A project narrows the answer the way it narrows a reference search: by name, by a multi-targeted
	/// project's name without its framework, and before the cut, so the total and the truncation
	/// describe the list that was asked for.
	/// </summary>
	[Test]
	public async Task Narrows_to_one_project_before_it_cuts_the_list()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var target = new SymbolTarget { Symbol = "Core.IStore" };

		var app = await NavigationService.FindImplementationsAsync(
			snapshot, target, 1, TestContext.Current!.Execution.CancellationToken, project: "App");

		app.Matches.ShouldHaveSingleItem().Name.ShouldBe("FileStore");
		app.TotalCount.ShouldBe(1);
		app.Truncated.ShouldBeFalse();
		app.Notices.ShouldContain(notice => notice.Contains("other than the one named", StringComparison.Ordinal));

		var core = await NavigationService.FindImplementationsAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Core");

		// Core without a framework is both of them, so it lists what either compiles.
		core.Matches.Select(match => match.Name).ShouldBe(["LegacyStore", "MemoryStore"], ignoreOrder: true);

		var everywhere = await NavigationService.FindImplementationsAsync(
			snapshot, target, 1, TestContext.Current!.Execution.CancellationToken);

		everywhere.Matches.Count.ShouldBe(1);
		everywhere.TotalCount.ShouldBe(3);
		everywhere.Truncated.ShouldBeTrue();
	}

	/// <summary>
	/// A project named with its framework lists what that framework's compilation declares, as that
	/// framework's copy. The search hands back one framework's copy of a type, so naming the other still
	/// has to find it; and a file both frameworks compile can declare a type for only one of them, behind
	/// <c>#if</c>, which the file alone would list for both.
	/// </summary>
	[Test]
	public async Task Narrows_a_multi_targeted_project_to_what_one_framework_declares()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		var target = new SymbolTarget { Symbol = "Core.IStore" };

		var older = await NavigationService.FindImplementationsAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Core(net9.0)");

		older.Matches.Select(match => match.Name).ShouldBe(["LegacyStore", "MemoryStore"], ignoreOrder: true);
		older.Matches.ShouldAllBe(match => match.Project == "Core(net9.0)");

		var newer = await NavigationService.FindImplementationsAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Core(net10.0)");

		newer.Matches.ShouldHaveSingleItem().Name.ShouldBe("MemoryStore");
		newer.Matches.ShouldAllBe(match => match.Project == "Core(net10.0)");
		newer.TotalCount.ShouldBe(1);
	}

	/// <summary>
	/// A project name the solution does not carry is refused, naming the ones it does. An empty list
	/// reads exactly like a type nothing implements.
	/// </summary>
	[Test]
	public async Task Refuses_to_narrow_to_a_project_that_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentException>(() =>
			NavigationService.FindImplementationsAsync(
				snapshot,
				new SymbolTarget { Symbol = "Core.IStore" },
				200,
				TestContext.Current!.Execution.CancellationToken,
				project: "Kernel")).OfExactType();

		error.Message.ShouldContain("Kernel", Case.Sensitive);
		error.Message.ShouldContain("App", Case.Sensitive);
	}
}
