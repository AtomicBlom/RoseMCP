using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which compiled methods make up the body somebody sees when they ask to look at one method.
/// <para>
/// This is what a position picker stands on. The lines a reader most wants to break on -- inside a
/// lambda, after an <c>await</c> -- are compiled into methods with names nobody has typed, so a
/// picker offering only the named method's own sequence points refuses exactly those lines while
/// appearing to work.
/// </para>
/// <para>
/// Against a real compilation rather than hand-written extents, because what the compiler does with
/// a lambda and an <c>async</c> body is the fact being relied on, and a fixture that states it
/// proves only that the test author believed it.
/// </para>
/// </summary>
public sealed class MethodRegionTests
{
	/// <summary>
	/// The three shapes that move code out of the method it was written in. Each body is one line so
	/// a test can name it by its text.
	/// </summary>
	private const string BodiesSource = """
		namespace Probe;

		using System;
		using System.Threading.Tasks;

		public static class Bodies
		{
			public static int WithLambda(int seed)
			{
				Func<int, int> twice = value => value * 3;
				return twice(seed);
			}

			public static int WithLocalFunction(int seed)
			{
				return Inner(seed);

				int Inner(int value) => value + 7;
			}

			public static async Task<int> WithAwait(int seed)
			{
				await Task.Yield();
				return seed + 11;
			}
		}
		""";

	/// <summary>A lambda's body is a method of its own, written inside the braces of the one that owns it.</summary>
	[Test]
	public void A_lambda_belongs_to_the_method_it_was_written_in()
	{
		using var fixture = CompiledModule.Of(BodiesSource);
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var extents = symbols!.Pdb!.Extents();

		var region = MethodRegion.Of(extents, CompiledModule.TokenOf(symbols.Metadata, "WithLambda"));

		Assert.Equal(2, region.Count);
		Assert.Contains(region, extent => Covers(extent, fixture.LineOf("value * 3")));
	}

	[Test]
	public void A_local_function_belongs_to_the_method_it_was_written_in()
	{
		using var fixture = CompiledModule.Of(BodiesSource);
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var extents = symbols!.Pdb!.Extents();

		var region = MethodRegion.Of(extents, CompiledModule.TokenOf(symbols.Metadata, "WithLocalFunction"));

		Assert.Equal(2, region.Count);
		Assert.Contains(region, extent => Covers(extent, fixture.LineOf("value + 7")));
	}

	/// <summary>
	/// The case containment cannot reach, and the one that decides the shape of the whole thing. An
	/// async method has no sequence points of its own at all -- every line a reader recognises
	/// belongs to a <c>MoveNext</c> on a generated type -- so a region seeded on the named method
	/// alone is empty for exactly the methods somebody most wants to look at.
	/// </summary>
	[Test]
	public void An_async_method_reaches_the_state_machine_holding_its_body()
	{
		using var fixture = CompiledModule.Of(BodiesSource);
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);
		var extents = symbols!.Pdb!.Extents();
		var token = CompiledModule.TokenOf(symbols.Metadata, "WithAwait");

		Assert.DoesNotContain(extents, extent => extent.MethodToken == token);

		var region = MethodRegion.Of(extents, token);
		var moveNext = Assert.Single(region);
		Assert.Equal(token, moveNext.KickoffToken);

		var afterTheAwait = fixture.LineOf("seed + 11");
		var lines = MethodRegion.Lines(region);
		Assert.NotNull(lines);
		Assert.True(
			lines!.Value.FirstLine <= afterTheAwait && afterTheAwait <= lines.Value.LastLine,
			$"the region a reader is shown covers lines {lines.Value.FirstLine}-{lines.Value.LastLine} "
				+ $"and the line after the await is {afterTheAwait}");
		Assert.Equal(fixture.SourcePath, lines.Value.File);
	}

	/// <summary>
	/// Nothing belongs to a method that is not there. A token from another module resolves to no
	/// region rather than to the first one in the table, which would show somebody a method they did
	/// not ask about and let them set a breakpoint in it.
	/// </summary>
	[Test]
	public void A_token_the_module_does_not_describe_has_no_region()
	{
		using var fixture = CompiledModule.Of(BodiesSource);
		using var symbols = ModuleSymbols.TryLoad(fixture.ModulePath);

		Assert.Empty(MethodRegion.Of(symbols!.Pdb!.Extents(), 0x06FFFFFF));
		Assert.Null(MethodRegion.Lines([]));
	}

	private static bool Covers(MethodExtent extent, int line) => extent.FirstLine <= line && line <= extent.LastLine;
}
