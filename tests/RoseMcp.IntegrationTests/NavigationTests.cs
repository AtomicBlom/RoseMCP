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
		foreach (var location in result.References)
		{
			location.FilePath.ShouldEndWith("Program.cs", Case.Insensitive);
		}
	}

	/// <summary>
	/// The three ways to ask for less, measured as sizes rather than asserted as flags. A widely used
	/// member answers at a size nothing can read, and maxResults is no answer to it: it drops
	/// references while the previews on the ones it keeps are most of the payload. Each narrowing has
	/// to be smaller than the full answer or it is not one.
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

		full.References.ShouldNotBeEmpty();
		foreach (var location in full.References)
		{
			location.Preview.ShouldNotBeNull();
		}

		var plain = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, includePreviews: false);

		// The count and the places are the same answer; only the lines of source are gone.
		plain.TotalCount.ShouldBe(full.TotalCount);
		plain.References.Count.ShouldBe(full.References.Count);
		foreach (var location in plain.References)
		{
			location.Preview.ShouldBeNull();
		}
		foreach (var location in plain.References)
		{
			location.ContainingMember.ShouldNotBeNull();
		}
		(Size(plain) < Size(full)).ShouldBeTrue();

		var counted = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, definitionsOnly: true);

		counted.References.ShouldBeEmpty();
		counted.Definitions.ShouldNotBeEmpty();
		counted.TotalCount.ShouldBe(full.TotalCount);
		counted.Truncated.ShouldBeTrue();
		(Size(counted) < Size(plain)).ShouldBeTrue();

		var scoped = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current!.Execution.CancellationToken, project: "Core");

		// Add is called from App and never from the project declaring it, so narrowing to Core empties
		// the list while the symbol goes on being used -- which the caller can tell apart only because
		// naming a project the solution does not have is refused instead.
		scoped.References.ShouldBeEmpty();
		foreach (var location in full.References)
		{
			location.Project.ShouldBe("App");
		}
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
		references.References.ShouldNotBeEmpty();
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

		var reference = references.References.ShouldHaveSingleItem();

		reference.FilePath.ShouldBe(fixture.Path("Simple", "App", "Program.cs"), StringCompareShould.IgnoreCase);
		reference.Line.ShouldBe(4);
		reference.Preview!.ShouldContain("Calculator.Multiply", Case.Sensitive);
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

		var reference = references.References.ShouldHaveSingleItem();

		reference.FilePath.ShouldBe(fixture.Path("Simple", "App", "Program.cs"), StringCompareShould.IgnoreCase);
		reference.Line.ShouldBe(4);
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

	/// <summary>Not asked for, not paid for: the field stays empty rather than always carrying a body.</summary>
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

		info.Source.ShouldBeEmpty();
	}
}
