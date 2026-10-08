using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

public sealed class NavigationTests
{
	[Test]
	public async Task Describes_a_symbol_from_its_declaration()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("Simple", "Core", "Calculator.cs"), Line = 7, Column = 20 },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("Multiply");
		info.Kind.ShouldBe("Method");
		info.Accessibility.ShouldBe("Public");
		info.Signature.ShouldContain("Core.Calculator.Multiply", Case.Sensitive);
		info.IsFromSource.ShouldBeTrue();
		info.Declarations.ShouldHaveSingleItem();
	}

	/// <summary>Pointing at a use site must work as well as pointing at the declaration.</summary>
	[Test]
	public async Task Describes_a_symbol_from_a_use_site_in_another_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("Simple", "App", "Program.cs"), Line = 4, Column = 30 },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("Multiply");
		info.Declarations.Single().FilePath.ShouldBe(
			fixture.Path("Simple", "Core", "Calculator.cs"), StringCompareShould.IgnoreCase);
	}

	/// <summary>
	/// A type from a referenced assembly is in every compilation this worker holds, so refusing to
	/// describe it because nothing in the solution declares it answers a narrower question than the one
	/// asked -- and sends the caller to a decompiler for something the compilation had to hand.
	/// </summary>
	[Test]
	public async Task Describes_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("StringBuilder");
		info.Kind.ShouldBe("NamedType");
		info.Namespace.ShouldBe("System.Text");

		// No file to point at, and the assembly said in its place: the two together are what tell a
		// caller this is not something it can edit.
		info.IsFromSource.ShouldBeFalse("a type from metadata has no source to edit");
		info.Declarations.ShouldBeEmpty();
		info.ContainingAssembly.ShouldNotBeNull();
	}

	/// <summary>
	/// What can be called on a library type is the question a caller has before writing against it, and
	/// a metadata type has no file for rose_outline to read -- so the answer about the type carries it,
	/// against the real framework, where overloads are many and some members are obsolete.
	/// </summary>
	[Test]
	public async Task Lists_what_can_be_called_on_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var builder = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current!.Execution.CancellationToken,
			members: "AppendLine");

		var appendLines = builder.Members.ShouldNotBeNull();

		appendLines.Count.ShouldBeGreaterThan(1, "AppendLine is overloaded");
		appendLines.ShouldAllBe(member => member.Name == "AppendLine" && member.Signature != null);
		appendLines.Select(member => member.Signature).ShouldContain("System.Text.StringBuilder System.Text.StringBuilder.AppendLine(string value)");
		builder.TotalMembers.ShouldBe(appendLines.Count);

		// A framework member obsolete as a warning, which is the mark a warnings-as-errors build fails on.
		var encoding = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.Encoding" },
			TestContext.Current!.Execution.CancellationToken,
			members: "UTF7");

		encoding.Members.ShouldNotBeNull().ShouldHaveSingleItem().Obsolete.ShouldBe("warning");

		// And the cap, said rather than left to read as the whole type.
		var capped = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current!.Execution.CancellationToken,
			maxMembers: 5);

		capped.Members.ShouldNotBeNull().Count.ShouldBe(5);
		capped.Truncated.ShouldBeTrue();
		capped.TotalMembers.ShouldNotBeNull().ShouldBeGreaterThan(5);
		capped.Notices.ShouldContain(notice => notice.Contains("stopping at maxMembers=5", StringComparison.Ordinal));

		// The indexer, which metadata names this[] -- not an identifier, and still a member a caller uses.
		var indexer = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current!.Execution.CancellationToken,
			members: "this[]");

		indexer.Members.ShouldNotBeNull().ShouldHaveSingleItem().Signature.ShouldNotBeNull().ShouldContain("this[int index]", Case.Sensitive);
	}

	/// <summary>
	/// A member of a metadata type, which is the half a caller reaches for after the type: the last
	/// segment is looked up on the type the rest of the name resolves to.
	/// </summary>
	[Test]
	public async Task Describes_a_member_of_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.Encoding.UTF8" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("UTF8");
		info.Kind.ShouldBe("Property");
		info.ContainingType!.ShouldContain("Encoding", Case.Sensitive);
		info.IsFromSource.ShouldBeFalse("a member from metadata has no source to edit");
	}

	/// <summary>
	/// The last segment of a library member's address is a name this solution almost certainly
	/// declares somewhere of its own -- Add, Name, Count, Document. Reaching metadata only when the
	/// name is carried nowhere at all would therefore refuse the library members most worth asking
	/// about, and would refuse more of them the larger the solution got. The fixture's own
	/// Calculator.Add is what makes this address one that a name search does find something for.
	/// </summary>
	[Test]
	public async Task Describes_a_metadata_member_whose_name_a_source_member_also_carries()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Collections.Generic.List.Add" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("Add");
		info.Kind.ShouldBe("Method");

		// The library one, not the source member that shares its name.
		info.ContainingType!.ShouldContain("List", Case.Sensitive);
		info.IsFromSource.ShouldBeFalse("a member from metadata has no source to edit");
	}

	/// <summary>
	/// A caller reading code sees StringBuilder, not System.Text.StringBuilder, and the source search
	/// accepts a bare last segment. Demanding full qualification only of metadata makes the tool
	/// stricter exactly where the caller knows least, and the refusal it produces points at
	/// rose_search_symbols, which searches source and so answers nothing here.
	/// </summary>
	[Test]
	public async Task Describes_a_metadata_type_named_without_its_namespace()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "StringBuilder" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("StringBuilder");
		info.Namespace.ShouldBe("System.Text");
		info.IsFromSource.ShouldBeFalse("a type from metadata has no source to edit");
	}

	/// <summary>
	/// Overloads in metadata are several symbols and must be refused as such. Answering with whichever
	/// one came first is the failure with no symptom: AppendLine() and AppendLine(string) differ only
	/// past the name, so a caller about to write the second reads the first's documentation and finds
	/// nothing in it that looks wrong. The refusal names the parameter list as the way out, because
	/// that is the argument that actually separates them.
	/// </summary>
	[Test]
	public async Task Refuses_to_choose_between_overloads_that_live_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentException>(() =>
			NavigationService.DescribeAsync(
				snapshot,
				new SymbolTarget { Symbol = "System.Text.StringBuilder.AppendLine" },
				TestContext.Current!.Execution.CancellationToken)).OfExactType();

		error.Message.ShouldContain("AppendLine", Case.Sensitive);
		error.Message.ShouldContain("Name the parameter types", Case.Sensitive);
	}

	/// <summary>
	/// And the way out works: the parameter list the refusal asks for picks one overload out of
	/// metadata, so the advice is something a caller can act on rather than something to read.
	/// </summary>
	[Test]
	public async Task Describes_the_metadata_overload_a_parameter_list_names()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder.AppendJoin(string, string[])" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("AppendJoin");
		info.Signature.ShouldContain("string separator", Case.Sensitive);
		info.IsFromSource.ShouldBeFalse("a member from metadata has no source to edit");
	}

	/// <summary>
	/// A type named for the namespace it is in. Read only as a constructor, its qualified name is
	/// answered with "declares no constructor ... add one with rose_add_member", which is advice about a
	/// type nobody asked about.
	/// </summary>
	[Test]
	public async Task Describes_a_type_named_for_the_namespace_it_is_in()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Gauge.Gauge" },
			TestContext.Current!.Execution.CancellationToken);

		info.Kind.ShouldBe("NamedType");
		info.Namespace.ShouldBe("Library.Gauge");
		info.IsFromSource.ShouldBeTrue();
	}

	/// <summary>
	/// A positional record property is read by name like any other member, whatever else in the
	/// solution is called Name.
	/// </summary>
	[Test]
	public async Task Finds_the_uses_of_a_positional_record_property_by_name()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Labelled.Name" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Address.ShouldBe("Library.Labelled.Name");
		result.Listed().ShouldContain(reference => reference.Site.Preview!.Contains("labelled.Name", StringComparison.Ordinal));
	}

	/// <summary>
	/// A constructor is the one address whose type part is written out in full, so it is also the one
	/// where falling back must stay narrow: a type this solution does declare keeps its own answer,
	/// and only a type declared nowhere here is looked for in a referenced assembly.
	/// </summary>
	[Test]
	public async Task Describes_a_constructor_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder..ctor(int)" },
			TestContext.Current!.Execution.CancellationToken);

		info.ContainingType!.ShouldContain("StringBuilder", Case.Sensitive);
		info.Signature.ShouldContain("int capacity", Case.Sensitive);
		info.IsFromSource.ShouldBeFalse("a constructor from metadata has no source to edit");
	}

	/// <summary>
	/// A name nothing carries anywhere still refuses, and with the refusal the source search wrote:
	/// falling back to metadata must not turn "nothing is called that" into a vaguer error.
	/// </summary>
	[Test]
	public async Task Still_refuses_a_name_that_is_in_neither_source_nor_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<SymbolNotFoundException>(() =>
			NavigationService.DescribeAsync(
				snapshot,
				new SymbolTarget { Symbol = "Nowhere.At.All.Whatsoever" },
				TestContext.Current!.Execution.CancellationToken)).OfExactType();

		error.Message.ShouldContain("Whatsoever", Case.Sensitive);
	}

	/// <summary>
	/// Who uses a type from a referenced assembly is a question about this solution's source, so
	/// refusing it because nothing here declares the type answers a narrower question than the one
	/// asked. The definitions come back empty -- a metadata symbol has no source location, which is
	/// what tells the caller there is nothing to edit -- and the uses are the answer.
	/// </summary>
	[Test]
	public async Task Finds_the_uses_of_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Console" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Definitions.ShouldBeEmpty();
		result.TotalCount.ShouldBe(2);
		result.Files.ShouldHaveSingleItem().FilePath.ShouldEndWith("Program.cs", Case.Insensitive);
		result.Listed().Count.ShouldBe(2);
	}

	/// <summary>
	/// The ways to ask for less, measured as sizes rather than asserted as flags. Each narrowing has to
	/// be smaller than the full answer or it is not one, and none of them may read as a symbol nobody
	/// uses.
	/// </summary>
	[Test]
	public async Task Narrows_a_large_answer_three_ways()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var target = new SymbolTarget { Symbol = "Core.Calculator.Add" };

		var full = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken);

		full.Listed().ShouldNotBeEmpty();
		full.Shape.ShouldBeNull("a list that is the whole answer needs no description of itself");
		foreach (var reference in full.Listed())
		{
			reference.Site.Preview.ShouldNotBeNull();
		}

		var plain = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, includePreviews: false);

		// The count and the places are the same answer; only the lines of source are gone.
		plain.TotalCount.ShouldBe(full.TotalCount);
		plain.Listed().Count.ShouldBe(full.Listed().Count);
		foreach (var reference in plain.Listed())
		{
			reference.Site.Preview.ShouldBeNull();
			reference.Site.ContainingMember.ShouldNotBeNull();
		}
		Size(plain).ShouldBeLessThan(Size(full));

		var counted = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, definitionsOnly: true);

		// Nothing listed because nothing was asked for, which raising maxResults would not change: so the
		// answer is not truncated, and it carries the shape the list would have had.
		counted.Files.ShouldBeEmpty();
		counted.Definitions.ShouldNotBeEmpty();
		counted.TotalCount.ShouldBe(full.TotalCount);
		counted.Truncated.ShouldBeFalse();
		counted.Notices.ShouldBeEmpty();
		counted.Shape.ShouldNotBeNull().Total.ShouldBe(full.TotalCount);

		var scoped = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Core");

		// Add is called from App and never from the project declaring it, so narrowing to Core keeps
		// nothing while the symbol goes on being used -- which the answer says, with the shape of every
		// use, rather than handing back an empty list that reads as dead code.
		scoped.Files.ShouldBeEmpty();
		scoped.TotalCount.ShouldBe(0);
		scoped.Notices.ShouldHaveSingleItem().ShouldContain("in project Core", Case.Sensitive);
		scoped.Shape.ShouldNotBeNull().Projects.ShouldHaveSingleItem().Project.ShouldBe("App");
		foreach (var file in full.Files)
		{
			file.Project.ShouldBe("App");
		}
	}

	/// <summary>
	/// Past maxResults the answer is the shape of the references rather than the first few of them, and
	/// a group read off that shape is a question the next call asks: passed back as containingMember, the
	/// busiest member lists exactly the references the shape counted in it.
	/// </summary>
	[Test]
	public async Task Answers_an_overflow_with_its_shape_and_lists_a_group_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var target = new SymbolTarget { Symbol = "System.String" };

		var overflowed = await NavigationService.FindReferencesAsync(
			snapshot, target, 3, TestContext.Current!.Execution.CancellationToken, includePreviews: false);

		overflowed.TotalCount.ShouldBeGreaterThan(3, "the fixture should use string more often than the cap");
		overflowed.Truncated.ShouldBeTrue();
		overflowed.Files.ShouldBeEmpty();

		var shape = overflowed.Shape.ShouldNotBeNull();
		shape.Total.ShouldBe(overflowed.TotalCount);
		shape.Projects.Sum(project => project.Count).ShouldBe(shape.Total);
		shape.MemberCount.ShouldBeGreaterThan(1);

		var notice = overflowed.Notices.ShouldHaveSingleItem();
		notice.ShouldContain("containingMember", Case.Sensitive);
		notice.ShouldContain($"maxResults={overflowed.TotalCount}", Case.Sensitive);

		// The shape is the smaller answer, which is the whole point of giving it instead of the list.
		var everything = await NavigationService.FindReferencesAsync(
			snapshot, target, overflowed.TotalCount, TestContext.Current!.Execution.CancellationToken, includePreviews: false);

		everything.Truncated.ShouldBeFalse();
		everything.Listed().Count.ShouldBe(overflowed.TotalCount);
		Size(overflowed).ShouldBeLessThan(Size(everything));

		var busiest = shape.Members[0];
		var asked = await NavigationService.FindReferencesAsync(
			snapshot,
			target,
			200,
			TestContext.Current!.Execution.CancellationToken,
			containingMember: busiest.ContainingMember);

		asked.TotalCount.ShouldBe(busiest.Count);
		asked.Shape.ShouldBeNull();
		asked.Listed().Count.ShouldBe(busiest.Count);
		asked.Listed().ShouldAllBe(reference => reference.Site.ContainingMember == busiest.ContainingMember);
	}

	/// <summary>
	/// A member nothing references from is said, with the shape of every reference so the member that
	/// does can be read off it, rather than answered with a list that reads as a symbol nobody uses.
	/// </summary>
	[Test]
	public async Task Says_when_no_reference_sits_in_the_member_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			200,
			TestContext.Current!.Execution.CancellationToken,
			containingMember: "Nowhere");

		result.Files.ShouldBeEmpty();
		result.TotalCount.ShouldBe(0);
		result.Truncated.ShouldBeFalse();
		result.Notices.ShouldHaveSingleItem().ShouldContain("inside a member called Nowhere", Case.Sensitive);
		result.Shape.ShouldNotBeNull().Members.ShouldHaveSingleItem().ContainingMember.ShouldBe("Caller.Call");

		// The member's name alone reaches it, which is what a caller has before it has seen an answer.
		var byName = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			200,
			TestContext.Current!.Execution.CancellationToken,
			containingMember: "Call");

		byName.Listed().ShouldHaveSingleItem().Site.ContainingMember.ShouldBe("Caller.Call");
	}

	/// <summary>
	/// A generator's output and the files somebody wrote are separable, and between them they are every
	/// reference: string is used both by the fixture's own class and by the source its generator emits.
	/// </summary>
	[Test]
	public async Task Separates_generated_references_from_written_ones()
	{
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		// By position, because the generator project's netstandard reference makes the name System.String
		// name two symbols, and the refusal of that is a separate matter from what this test is about.
		var target = new SymbolTarget { FilePath = fixture.Path("WithGenerator", "Consumer", "Widget.cs"), Line = 8, Column = 9 };

		var all = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, definitionsOnly: true, project: "Consumer");

		var generated = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Consumer", isGenerated: true);

		var written = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Consumer", isGenerated: false);

		generated.Files.ShouldNotBeEmpty();
		generated.Files.ShouldAllBe(file => file.GeneratedHintName != null);
		written.Files.ShouldNotBeEmpty();
		written.Files.ShouldAllBe(file => file.GeneratedHintName == null);

		var shape = all.Shape.ShouldNotBeNull();
		shape.InGeneratedCode.ShouldBe(generated.TotalCount);
		(generated.TotalCount + written.TotalCount).ShouldBe(all.TotalCount);
	}

	/// <summary>
	/// A use from a test is a different fact from a use in the product, so the two are separable -- and
	/// asking for the product side of a symbol only tests use is said, not answered with nothing.
	/// </summary>
	[Test]
	public async Task Separates_references_in_test_projects()
	{
		using var fixture = FixtureSolution.Copy("Assertions", "Assertions.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var target = new SymbolTarget { Symbol = "Xunit.Assert" };

		var tests = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, isTestProject: true);

		tests.Files.ShouldNotBeEmpty();
		tests.Files.ShouldAllBe(file => file.IsTestProject);
		tests.Notices.ShouldBeEmpty();

		var product = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, isTestProject: false);

		product.Files.ShouldBeEmpty();
		product.Notices.ShouldHaveSingleItem().ShouldContain("outside test projects", Case.Sensitive);

		var shape = product.Shape.ShouldNotBeNull();
		shape.Total.ShouldBe(tests.TotalCount);
		shape.InTestProjects.ShouldBe(shape.Total);
	}

	/// <summary>
	/// A property is declared once, though its search cascades through both accessors, each declared by
	/// a keyword inside the property's own declaration.
	/// </summary>
	[Test]
	public async Task Lists_a_property_declared_once_once()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Count" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Definitions.ShouldHaveSingleItem().FilePath.ShouldEndWith("Greeter.cs", Case.Insensitive);
	}

	/// <summary>
	/// An address a result reports is one the next call takes. The signature beside it is for reading
	/// and does not parse: it leads with the return type, so the space before the second qualified name
	/// lands inside a segment, and it names the parameters, which are not their types. A caller who
	/// read a symbol out of one answer and wanted to edit it had to take the string apart by hand.
	/// </summary>
	[Test]
	public async Task Reports_an_address_the_next_call_takes()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var found = await NavigationService.SearchAsync(snapshot, "Notify", 50, TestContext.Current!.Execution.CancellationToken);

		var match = found.Matches.First(candidate => candidate.Signature.Contains("Notifier.Notify", StringComparison.Ordinal));

		match.Address.ShouldNotBeNull();

		// The whole claim: the address goes back in as symbol, and answers about the same member.
		var described = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = match.Address },
			TestContext.Current!.Execution.CancellationToken);

		described.Signature.ShouldBe(match.Signature);
		described.Address.ShouldBe(match.Address);

		var references = await NavigationService.FindReferencesAsync(
			snapshot, new SymbolTarget { Symbol = described.Address }, 200, TestContext.Current!.Execution.CancellationToken);

		references.Address.ShouldBe(match.Address);
		references.Files.ShouldNotBeEmpty();
	}

	/// <summary>
	/// An overload is separated by its parameter types, which is what makes the address usable on the
	/// members most likely to have one: a name alone is refused where two declarations carry it.
	/// </summary>
	[Test]
	public async Task Reports_an_address_that_separates_an_overload()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var found = await NavigationService.SearchAsync(snapshot, "Greet", 50, TestContext.Current!.Execution.CancellationToken);

		var addresses = found.Matches
			.Where(match => match.Name == "Greet")
			.Select(match => match.Address)
			.ToArray();

		addresses.Length.ShouldBe(2);
		addresses.ShouldContain("Library.Greeter.Greet(string)");
		addresses.ShouldContain("Library.Greeter.Greet(string, string)");

		foreach (var address in addresses)
		{
			var described = await NavigationService.DescribeAsync(
				snapshot, new SymbolTarget { Symbol = address }, TestContext.Current!.Execution.CancellationToken);

			described.Address.ShouldBe(address);
		}
	}

	/// <summary>
	/// A project name the solution does not carry is refused rather than filtered on. An empty list
	/// reads exactly like a symbol nobody uses, and that is the answer that invites a deletion.
	/// </summary>
	[Test]
	public async Task Refuses_to_narrow_to_a_project_that_is_not_there()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentException>(() =>
			NavigationService.FindReferencesAsync(
				snapshot,
				new SymbolTarget { Symbol = "Core.Calculator.Add" },
				200,
				TestContext.Current!.Execution.CancellationToken,
				project: "Kernel")).OfExactType();

		error.Message.ShouldContain("Kernel", Case.Sensitive);
		error.Message.ShouldContain("Core", Case.Sensitive);
	}

	/// <summary>
	/// One declaration is one definition, and one use one reference. An automatic property's accessors
	/// and backing field are symbols the search cascades to, each declared inside the property, and a
	/// multi-targeted project compiles the property once per framework -- so without the merge one line
	/// of source is listed as several definitions, and a caller counting them gets a wrong answer.
	/// </summary>
	[Test]
	public async Task Lists_a_property_declaration_once()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Core.MemoryStore.Capacity" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		var definition = result.Definitions.ShouldHaveSingleItem();

		definition.Preview.ShouldNotBeNull().ShouldContain("Capacity { get; init; }", Case.Sensitive);

		// And one use is one reference, though the search reaches it once through each framework's copy.
		var file = result.Files.ShouldHaveSingleItem();
		file.Project.ShouldBe("App");
		file.References.ShouldHaveSingleItem();
		result.TotalCount.ShouldBe(1);
	}

	/// <summary>
	/// A file two projects compile holds a use in each, and the two stay two: merged, one project drops
	/// out of the shape and the use counts on only one side of isTestProject, so the two sides no longer
	/// add up to the whole. The copies one multi-targeted project reaches do merge, into the same project
	/// on every call.
	/// </summary>
	[Test]
	public async Task Keeps_a_use_in_a_file_two_projects_compile_once_in_each()
	{
		using var fixture = FixtureSolution.Copy("Hierarchy", "Hierarchy.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var snapshot = await session.ReadAsync(cancellationToken);
		var target = new SymbolTarget { Symbol = "Core.Labels.Of" };

		var all = await NavigationService.FindReferencesAsync(snapshot, target, 200, cancellationToken);

		all.TotalCount.ShouldBe(3);
		all.Files.ShouldAllBe(file => file.References.Count == 1);
		all.Files
			.Where(file => file.FilePath.EndsWith("Shelf.cs", StringComparison.Ordinal))
			.Select(file => file.Project)
			.ShouldBe(["App", "App.Tests"], ignoreOrder: true);
		all.Definitions.ShouldHaveSingleItem();

		var shape = (await NavigationService.FindReferencesAsync(
			snapshot, target, 200, cancellationToken, definitionsOnly: true)).Shape.ShouldNotBeNull();

		shape.Total.ShouldBe(all.TotalCount);
		shape.InTestProjects.ShouldBe(1);
		shape.Projects.Select(project => project.Project).ShouldContain("App");
		shape.Projects.Select(project => project.Project).ShouldContain("App.Tests");

		var tests = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, cancellationToken, isTestProject: true);
		var product = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, cancellationToken, isTestProject: false);

		tests.TotalCount.ShouldBe(1);
		(tests.TotalCount + product.TotalCount).ShouldBe(all.TotalCount);

		// Core's own use is reached through each framework's copy and kept as one of them, the same one
		// every time, so a caller narrowing to the project a reference named finds it there again.
		var inCore = all.Files.Where(file => file.FilePath.EndsWith("Stores.cs", StringComparison.Ordinal)).ShouldHaveSingleItem();
		inCore.Project.ShouldBe("Core(net10.0)");

		for (var call = 0; call < 3; call++)
		{
			var again = await NavigationService.FindReferencesAsync(snapshot, target, 200, cancellationToken);

			again.Files.Where(file => file.FilePath.EndsWith("Stores.cs", StringComparison.Ordinal)).ShouldHaveSingleItem()
				.Project.ShouldBe(inCore.Project);
		}
	}

	/// <summary>
	/// A positional record's property is declared by its parameter, at the same place, and the search
	/// finds both. It is still one definition.
	/// </summary>
	[Test]
	public async Task Lists_a_positional_record_property_declaration_once()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Labelled.Name" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		result.Definitions.ShouldHaveSingleItem();
	}

	/// <summary>How big an answer is on the wire, which is the thing the narrowing exists to change.</summary>
	private static int Size(ReferencesResult result) =>
		System.Text.Json.JsonSerializer.Serialize(result, ContractJson.Options).Length;

	[Test]
	public async Task Finds_references_across_project_boundaries()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var target = new SymbolTarget
		{
			FilePath = fixture.Path("Simple", "Core", "Calculator.cs"),
			Line = 7,
			Column = 20,
		};

		var references = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken);

		var reference = references.Listed().ShouldHaveSingleItem();

		reference.File.FilePath.ShouldBe(fixture.Path("Simple", "App", "Program.cs"), StringCompareShould.IgnoreCase);
		reference.Site.Line.ShouldBe(4);
		reference.Site.Preview!.ShouldContain("Calculator.Multiply", Case.Sensitive);
	}

	[Test]
	public async Task Explains_a_position_that_is_not_a_symbol()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentOutOfRangeException>(
			() => NavigationService.DescribeAsync(
				snapshot,
				new SymbolTarget { FilePath = fixture.Path("Simple", "Core", "Calculator.cs"), Line = 9999, Column = 1 },
				TestContext.Current!.Execution.CancellationToken)).OfExactType();

		// Guessing at a line number should not produce an opaque index error.
		error.Message.ShouldContain("line(s)", Case.Sensitive);
	}

	[Test]
	public async Task Searches_by_abbreviation()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await NavigationService.SearchAsync(snapshot, "Calc", 50, TestContext.Current!.Execution.CancellationToken);

		result.Matches.ShouldContain(match => match.Name == "Calculator" && match.Kind == "NamedType");
	}

	/// <summary>
	/// A name is what a caller has when it has not read the file, which is the case worth serving:
	/// needing a line and column means grepping for one first, and the position is wrong as soon as
	/// an earlier edit lands.
	/// </summary>
	[Test]
	public async Task Describes_a_symbol_named_rather_than_pointed_at()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Core.Calculator.Multiply" },
			TestContext.Current!.Execution.CancellationToken);

		info.Name.ShouldBe("Multiply");
		info.Kind.ShouldBe("Method");
		info.Signature.ShouldContain("Core.Calculator.Multiply", Case.Sensitive);
	}

	/// <summary>
	/// Where a declaration stops is what a text splice has to guess and what it gets wrong. The
	/// compiler knows it, and the doc comment above the member counts as part of it, since replacing
	/// the member without it leaves the documentation stranded above the wrong thing.
	/// </summary>
	[Test]
	public async Task Reports_where_the_declaration_begins_and_ends()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current!.Execution.CancellationToken);

		var span = info.DeclarationSpans.ShouldHaveSingleItem();
		var lines = await File.ReadAllLinesAsync(span.FilePath, TestContext.Current!.Execution.CancellationToken);

		span.FilePath.ShouldEndWith("Greeter.cs", Case.Insensitive);
		span.LineCount.ShouldBe(5);

		// The documentation comment is the first line of it, and the closing brace the last.
		lines[span.StartLine - 1].ShouldContain("/// <summary>The greeting for one name.</summary>", Case.Sensitive);
		lines[span.EndLine - 1].ShouldBe("\t}");
	}

	/// <summary>A partial has a declaration in each of its files, and both are worth knowing.</summary>
	[Test]
	public async Task Reports_a_span_for_each_declaration_of_a_partial()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Split" },
			TestContext.Current!.Execution.CancellationToken);

		info.DeclarationSpans.Count.ShouldBe(2);
		info.DeclarationSpans.ShouldContain(span => span.FilePath.EndsWith("Split.cs", StringComparison.OrdinalIgnoreCase));
		info.DeclarationSpans.ShouldContain(span => span.FilePath.EndsWith("SplitAgain.cs", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Neither addressing given is a mistake rather than a default, since guessing which was meant
	/// would answer confidently about some other symbol.
	/// </summary>
	[Test]
	public async Task Refuses_a_request_that_names_nothing_and_points_nowhere()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => NavigationService.DescribeAsync(
				snapshot, new SymbolTarget(), TestContext.Current!.Execution.CancellationToken)).OfExactType();

		error.Message.ShouldContain("Name the symbol", Case.Sensitive);
	}

	/// <summary>
	/// The same references, reached by name. A position has to be found by reading the file first,
	/// and a mis-counted column lands on a different identifier and answers completely, correctly and
	/// about the wrong symbol -- which is silent in a way a write's refusal is not.
	/// </summary>
	[Test]
	public async Task Finds_references_by_name()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Core.Calculator.Multiply" },
			200,
			TestContext.Current!.Execution.CancellationToken);

		var reference = references.Listed().ShouldHaveSingleItem();

		reference.File.FilePath.ShouldBe(fixture.Path("Simple", "App", "Program.cs"), StringCompareShould.IgnoreCase);
		reference.Site.Line.ShouldBe(4);
	}

	/// <summary>
	/// A name that matches nothing says so, and says what to do about a local or a parameter, which is
	/// the one thing a name cannot reach.
	/// </summary>
	[Test]
	public async Task Refuses_a_reference_search_that_names_nothing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => NavigationService.FindReferencesAsync(
				snapshot, new SymbolTarget(), 200, TestContext.Current!.Execution.CancellationToken)).OfExactType();

		thrown.Message.ShouldContain("local variable or a parameter", Case.Sensitive);
	}

	/// <summary>
	/// The source with the answer, so understanding a member does not end in a file read -- which is
	/// the moment the file is in front of the caller and the next edit goes through a text tool.
	/// </summary>
	[Test]
	public async Task Returns_the_source_of_a_member_when_asked()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current!.Execution.CancellationToken,
			includeSource: true);

		var source = info.Source.ShouldHaveSingleItem();

		source.ShouldContain("public string Greet(string name)", Case.Sensitive);
		source.ShouldContain("return $\"{_prefix}, {name}!\";", Case.Sensitive);

		// The documentation comment comes with it: half of what a reader wanted the source for.
		source.ShouldContain("<summary>The greeting for one name.</summary>", Case.Sensitive);
	}

	/// <summary>
	/// The documentation is its summary as prose, never the XML: the markup and the fully qualified
	/// references are most of the XML's length and none of its meaning, and a reference rendered as its
	/// name keeps the sentence whole. Every paragraph of it, unlike an outline's one sentence.
	/// </summary>
	[Test]
	public async Task Gives_the_summary_rendered_rather_than_its_xml()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var greet = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string, string)" },
			TestContext.Current!.Execution.CancellationToken);

		greet.Summary.ShouldBe("The greeting for name with a title, its prefix PrefixLength characters long.");

		var literal = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Literal" },
			TestContext.Current!.Execution.CancellationToken);

		literal.Summary.ShouldNotBeNull().ShouldStartWith("A multi-line raw string literal");
		literal.Summary.ShouldEndWith("no formatter and no analyzer has an opinion about.");
		literal.Summary.ShouldNotContain("<");
	}

	/// <summary>
	/// Not asked for, not paid for: the field is absent rather than empty, since an empty list reads as a
	/// symbol with no source rather than a question nobody asked.
	/// </summary>
	[Test]
	public async Task Leaves_the_source_out_unless_it_is_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current!.Execution.CancellationToken);

		info.Source.ShouldBeNull();
	}
}
