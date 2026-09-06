namespace RoseMcp.IntegrationTests;

public sealed class NavigationTests
{
	[Fact]
	public async Task Describes_a_symbol_from_its_declaration()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("Simple", "Core", "Calculator.cs"), Line = 7, Column = 20 },
			TestContext.Current.CancellationToken);

		Assert.Equal("Multiply", info.Name);
		Assert.Equal("Method", info.Kind);
		Assert.Equal("Public", info.Accessibility);
		Assert.Contains("Core.Calculator.Multiply", info.Signature, StringComparison.Ordinal);
		Assert.True(info.IsFromSource);
		Assert.Single(info.Declarations);
	}

	/// <summary>Pointing at a use site must work as well as pointing at the declaration.</summary>
	[Fact]
	public async Task Describes_a_symbol_from_a_use_site_in_another_project()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { FilePath = fixture.Path("Simple", "App", "Program.cs"), Line = 4, Column = 30 },
			TestContext.Current.CancellationToken);

		Assert.Equal("Multiply", info.Name);
		Assert.Equal(
			fixture.Path("Simple", "Core", "Calculator.cs"),
			info.Declarations.Single().FilePath,
			ignoreCase: true);
	}

	/// <summary>
	/// A type from a referenced assembly is in every compilation this worker holds, so refusing to
	/// describe it because nothing in the solution declares it answers a narrower question than the one
	/// asked -- and sends the caller to a decompiler for something the compilation had to hand.
	/// </summary>
	[Fact]
	public async Task Describes_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current.CancellationToken);

		Assert.Equal("StringBuilder", info.Name);
		Assert.Equal("NamedType", info.Kind);
		Assert.Equal("System.Text", info.Namespace);

		// No file to point at, and the assembly said in its place: the two together are what tell a
		// caller this is not something it can edit.
		Assert.False(info.IsFromSource);
		Assert.Empty(info.Declarations);
		Assert.NotNull(info.ContainingAssembly);
	}

	/// <summary>
	/// A member of a metadata type, which is the half a caller reaches for after the type: the last
	/// segment is looked up on the type the rest of the name resolves to.
	/// </summary>
	[Fact]
	public async Task Describes_a_member_of_a_type_that_lives_in_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.Encoding.UTF8" },
			TestContext.Current.CancellationToken);

		Assert.Equal("UTF8", info.Name);
		Assert.Equal("Property", info.Kind);
		Assert.Contains("Encoding", info.ContainingType, StringComparison.Ordinal);
		Assert.False(info.IsFromSource);
	}

	/// <summary>
	/// A name nothing carries anywhere still refuses, and with the refusal the source search wrote:
	/// falling back to metadata must not turn "nothing is called that" into a vaguer error.
	/// </summary>
	[Fact]
	public async Task Still_refuses_a_name_that_is_in_neither_source_nor_metadata()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<SymbolNotFoundException>(() =>
			NavigationService.DescribeAsync(
				snapshot,
				new SymbolTarget { Symbol = "Nowhere.At.All.Whatsoever" },
				TestContext.Current.CancellationToken));

		Assert.Contains("Whatsoever", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Finds_references_across_project_boundaries()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var target = new SymbolTarget
		{
			FilePath = fixture.Path("Simple", "Core", "Calculator.cs"),
			Line = 7,
			Column = 20,
		};

		var references = await NavigationService.FindReferencesAsync(
			snapshot, target, 200, TestContext.Current.CancellationToken);

		var reference = Assert.Single(references.References);

		Assert.Equal(fixture.Path("Simple", "App", "Program.cs"), reference.FilePath, ignoreCase: true);
		Assert.Equal(4, reference.Line);
		Assert.Contains("Calculator.Multiply", reference.Preview!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Explains_a_position_that_is_not_a_symbol()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => NavigationService.DescribeAsync(
				snapshot,
				new SymbolTarget { FilePath = fixture.Path("Simple", "Core", "Calculator.cs"), Line = 9999, Column = 1 },
				TestContext.Current.CancellationToken));

		// Guessing at a line number should not produce an opaque index error.
		Assert.Contains("line(s)", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Searches_by_abbreviation()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var result = await NavigationService.SearchAsync(snapshot, "Calc", 50, TestContext.Current.CancellationToken);

		Assert.Contains(result.Matches, match => match.Name == "Calculator" && match.Kind == "NamedType");
	}

	/// <summary>
	/// A name is what a caller has when it has not read the file, which is the case worth serving:
	/// needing a line and column means grepping for one first, and the position is wrong as soon as
	/// an earlier edit lands.
	/// </summary>
	[Fact]
	public async Task Describes_a_symbol_named_rather_than_pointed_at()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Core.Calculator.Multiply" },
			TestContext.Current.CancellationToken);

		Assert.Equal("Multiply", info.Name);
		Assert.Equal("Method", info.Kind);
		Assert.Contains("Core.Calculator.Multiply", info.Signature, StringComparison.Ordinal);
	}

	/// <summary>
	/// Where a declaration stops is what a text splice has to guess and what it gets wrong. The
	/// compiler knows it, and the doc comment above the member counts as part of it, since replacing
	/// the member without it leaves the documentation stranded above the wrong thing.
	/// </summary>
	[Fact]
	public async Task Reports_where_the_declaration_begins_and_ends()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current.CancellationToken);

		var span = Assert.Single(info.DeclarationSpans);
		var lines = await File.ReadAllLinesAsync(span.FilePath, TestContext.Current.CancellationToken);

		Assert.EndsWith("Greeter.cs", span.FilePath, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(5, span.LineCount);

		// The documentation comment is the first line of it, and the closing brace the last.
		Assert.Contains("/// <summary>The greeting for one name.</summary>", lines[span.StartLine - 1], StringComparison.Ordinal);
		Assert.Equal("\t}", lines[span.EndLine - 1]);
	}

	/// <summary>A partial has a declaration in each of its files, and both are worth knowing.</summary>
	[Fact]
	public async Task Reports_a_span_for_each_declaration_of_a_partial()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Split" },
			TestContext.Current.CancellationToken);

		Assert.Equal(2, info.DeclarationSpans.Count);
		Assert.Contains(info.DeclarationSpans, span => span.FilePath.EndsWith("Split.cs", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(info.DeclarationSpans, span => span.FilePath.EndsWith("SplitAgain.cs", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Neither addressing given is a mistake rather than a default, since guessing which was meant
	/// would answer confidently about some other symbol.
	/// </summary>
	[Fact]
	public async Task Refuses_a_request_that_names_nothing_and_points_nowhere()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => NavigationService.DescribeAsync(
				snapshot, new SymbolTarget(), TestContext.Current.CancellationToken));

		Assert.Contains("Name the symbol", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The same references, reached by name. A position has to be found by reading the file first,
	/// and a mis-counted column lands on a different identifier and answers completely, correctly and
	/// about the wrong symbol -- which is silent in a way a write's refusal is not.
	/// </summary>
	[Fact]
	public async Task Finds_references_by_name()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Core.Calculator.Multiply" },
			200,
			TestContext.Current.CancellationToken);

		var reference = Assert.Single(references.References);

		Assert.Equal(fixture.Path("Simple", "App", "Program.cs"), reference.FilePath, ignoreCase: true);
		Assert.Equal(4, reference.Line);
	}

	/// <summary>
	/// A name that matches nothing says so, and says what to do about a local or a parameter, which is
	/// the one thing a name cannot reach.
	/// </summary>
	[Fact]
	public async Task Refuses_a_reference_search_that_names_nothing()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => NavigationService.FindReferencesAsync(
				snapshot, new SymbolTarget(), 200, TestContext.Current.CancellationToken));

		Assert.Contains("local variable or a parameter", thrown.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The source with the answer, so understanding a member does not end in a file read -- which is
	/// the moment the file is in front of the caller and the next edit goes through a text tool.
	/// </summary>
	[Fact]
	public async Task Returns_the_source_of_a_member_when_asked()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current.CancellationToken,
			includeSource: true);

		var source = Assert.Single(info.Source);

		Assert.Contains("public string Greet(string name)", source, StringComparison.Ordinal);
		Assert.Contains("return $\"{_prefix}, {name}!\";", source, StringComparison.Ordinal);

		// The documentation comment comes with it: half of what a reader wanted the source for.
		Assert.Contains("<summary>The greeting for one name.</summary>", source, StringComparison.Ordinal);
	}

	/// <summary>Not asked for, not paid for: the field stays empty rather than always carrying a body.</summary>
	[Fact]
	public async Task Leaves_the_source_out_unless_it_is_asked_for()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current.CancellationToken);

		var info = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter.Greet(string)" },
			TestContext.Current.CancellationToken);

		Assert.Empty(info.Source);
	}
}
