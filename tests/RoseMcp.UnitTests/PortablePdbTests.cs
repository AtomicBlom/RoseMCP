using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading a module's portable PDB: whether the symbols belong to it, what its locals are called at
/// a given instruction, and which line an IL offset came from.
/// <para>
/// This is the difference between a readable stopped frame and a numbered one, and every failure it
/// can have is a confident wrong answer rather than an absent one -- an inner block's name given to
/// a slot the outer block owns, a line read out of a build that is not running. So each of them is
/// arranged deliberately here against a module compiled for the purpose.
/// </para>
/// </summary>
public sealed class PortablePdbTests
{
	[Test]
	public void The_symbols_beside_a_module_load()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);

		Assert.NotNull(symbols);
		Assert.Equal(PdbState.Loaded, symbols!.PdbState);
		Assert.NotNull(symbols.Pdb);
		Assert.Null(symbols.PdbProblem);
		Assert.Equal("Probe.pdb", Path.GetFileName(symbols.Pdb!.Path));
	}

	/// <summary>
	/// The nesting is the whole reason scopes are read rather than flattened: a slot belongs to
	/// whichever block is executing, and naming every slot from every scope at once hands back two
	/// names for one slot.
	/// </summary>
	[Test]
	public void Scopes_nest_and_a_block_local_is_not_in_scope_at_the_method_start()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var token = CompiledModule.TokenOf(symbols!.Metadata, "Nested");
		var pdb = symbols.Pdb!;

		var scopes = pdb.LocalScopes(token);
		Assert.Equal(2, scopes.Count);

		// Outermost first, which is what lets an inner block's name replace an outer one for a slot
		// they share.
		var outer = scopes[0];
		var inner = scopes[1];
		Assert.Contains(outer.Locals, local => local.Name == "outerTotal");
		Assert.Contains(inner.Locals, local => local.Name == "innerStep");

		Assert.True(inner.StartOffset > outer.StartOffset, "the block's IL starts after the method's");
		Assert.True(
			inner.StartOffset + inner.Length <= outer.StartOffset + outer.Length,
			"the block's IL ends inside the method's");
		Assert.True(outer.Covers(0), "the method's own scope covers its first instruction");
		Assert.False(inner.Covers(0), "the block's scope does not");

		// So at the first instruction there is one local to name, and inside the block there are two.
		var atStart = pdb.LocalNames(token, 0);
		Assert.Equal("outerTotal", Assert.Single(atStart).Value);

		var inBlock = pdb.LocalNames(token, inner.StartOffset);
		Assert.Equal(2, inBlock.Count);
		Assert.Contains(inBlock, named => named.Value == "outerTotal");
		Assert.Contains(inBlock, named => named.Value == "innerStep");
	}

	/// <summary>
	/// An IL offset resolves to the file the compiler recorded and a line inside the method. The
	/// lines are named by the code on them rather than by number, so editing the fixture cannot make
	/// this pass for the wrong reason.
	/// </summary>
	[Test]
	public void An_il_offset_names_the_file_and_the_line_it_came_from()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var token = CompiledModule.TokenOf(symbols!.Metadata, "Nested");
		var pdb = symbols.Pdb!;

		var atStart = pdb.Position(token, 0);
		Assert.NotNull(atStart);
		Assert.Equal(fixture.SourcePath, atStart!.File);

		// The method's first instruction is its opening brace, the line above its first statement.
		Assert.Equal(fixture.LineOf("var outerTotal") - 1, atStart.Line);

		var lines = pdb.SequencePointsOf(token)
			.Where(point => !point.IsHidden)
			.Select(point => point.Position!.Line)
			.ToList();
		Assert.Contains(fixture.LineOf("var innerStep"), lines);
		Assert.Contains(fixture.LineOf("return outerTotal"), lines);

		// An offset past the end of the method belongs to its last point rather than to nothing:
		// the search is for the last point at or before the offset.
		Assert.NotNull(pdb.Position(token, 9000));
	}

	/// <summary>
	/// A PDB from another build reads perfectly well and describes code that is not running, which
	/// makes it the one symbol failure worth shouting about. It is refused, and the refusal is told
	/// apart from having no symbols at all by whether a file is there.
	/// </summary>
	[Test]
	public void Symbols_from_another_build_are_refused_rather_than_read()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var other = CompiledModule.Of("public static class Other { public static int Two() => 2; }", "Other");
		var stale = File.ReadAllBytes(other.PdbPath);

		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath, _ => new MemoryStream(stale, writable: false));

		Assert.NotNull(symbols);
		Assert.Equal(PdbState.Mismatched, symbols!.PdbState);
		Assert.Null(symbols.Pdb);
		Assert.NotNull(symbols.PdbProblem);
		Assert.Contains("Probe.pdb", symbols.PdbProblem);
	}

	/// <summary>
	/// A module naming no symbols is an ordinary state and not a failure to read the module: the
	/// metadata is still there, which is what names methods and parameters.
	/// </summary>
	[Test]
	public void A_module_with_no_symbols_reads_anyway_and_says_which_state_that_is()
	{
		using var fixture = CompiledModule.NestedLocals(withSymbols: false);
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);

		Assert.NotNull(symbols);
		Assert.Equal(PdbState.NotFound, symbols!.PdbState);
		Assert.Null(symbols.Pdb);
		Assert.Null(symbols.PdbProblem);
		Assert.NotEqual(0, CompiledModule.TokenOf(symbols.Metadata, "Nested"));
	}

	/// <summary>A path that is not a managed assembly, and one that is not a file, both read as nothing.</summary>
	[Test]
	public void A_path_that_is_not_a_managed_assembly_reads_as_nothing()
	{
		using var fixture = CompiledModule.NestedLocals();

		Assert.Null(ModuleSymbols.TryLoad(fixture.SourcePath));
		Assert.Null(ModuleSymbols.TryLoad(Path.Combine(Path.GetTempPath(), $"rose-absent-{Guid.NewGuid():n}.dll")));
	}

	/// <summary>
	/// A token this PDB does not describe is answered with nothing rather than thrown at. A stack
	/// walk reaches methods from every module loaded, and one unreadable frame must not lose the rest
	/// of the stack.
	/// </summary>
	[Test]
	public void A_token_the_symbols_do_not_describe_is_answered_with_nothing()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var pdb = symbols!.Pdb!;

		// A method definition token (0x06) for a row this module does not have.
		const int Absent = 0x06000000 | 0x00FFFFFF;

		Assert.Empty(pdb.LocalScopes(Absent));
		Assert.Empty(pdb.LocalNames(Absent, 0));
		Assert.Empty(pdb.SequencePointsOf(Absent));
		Assert.Null(pdb.Position(Absent, 0));
	}
}
