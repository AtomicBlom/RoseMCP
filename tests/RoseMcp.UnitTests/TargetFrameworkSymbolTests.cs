namespace RoseMcp.UnitTests;

/// <summary>
/// targetFramework is meant to be the signal that a solution loaded under a configuration it does
/// not declare -- a project with no framework resolved nothing. Every project that reports null
/// without being broken empties that signal, and before this it was null for every single-targeted
/// project in every solution.
/// </summary>
public sealed class TargetFrameworkSymbolTests
{
	[Theory]
	// .NET Framework spells the version with no separator.
	[InlineData("net48", "NETFRAMEWORK", "NET48", "NET20_OR_GREATER", "NET48_OR_GREATER")]
	[InlineData("net472", "NETFRAMEWORK", "NET472", "NET472_OR_GREATER")]
	// Modern .NET separates major and minor.
	[InlineData("net10.0", "NET", "NETCOREAPP", "NET10_0", "NET5_0_OR_GREATER", "NET10_0_OR_GREATER")]
	[InlineData("net8.0", "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER")]
	// What eleven healthy projects in the Revit monorepo are, and reported nothing for.
	[InlineData("netstandard2.0", "NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_0_OR_GREATER")]
	[InlineData("netcoreapp3.1", "NETCOREAPP", "NETCOREAPP3_1", "NETCOREAPP3_1_OR_GREATER")]
	public void Reads_the_target_out_of_the_symbols(string expected, params string[] symbols) =>
		Assert.Equal(expected, TargetFrameworkSymbols.Infer(symbols));

	/// <summary>
	/// The _OR_GREATER symbols name every target below this one as well, so taking any of them would
	/// report the oldest framework the project is merely compatible with.
	/// </summary>
	[Fact]
	public void Ignores_the_or_greater_symbols_naming_older_targets()
	{
		var symbols = new[] { "NET", "NETCOREAPP", "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET10_0" };

		Assert.Equal("net10.0", TargetFrameworkSymbols.Infer(symbols));
	}

	/// <summary>
	/// NETFRAMEWORK, NETCOREAPP and a bare NET name a family, not a target. Answering with one would
	/// be worse than answering with nothing.
	/// </summary>
	[Theory]
	[InlineData("NETFRAMEWORK")]
	[InlineData("NETCOREAPP")]
	[InlineData("NET")]
	[InlineData("NETSTANDARD")]
	[InlineData("DEBUG")]
	[InlineData("TRACE")]
	public void Says_nothing_for_a_symbol_that_names_no_target(string symbol) =>
		Assert.Null(TargetFrameworkSymbols.Infer([symbol]));

	[Fact]
	public void Says_nothing_when_there_are_no_symbols_at_all()
	{
		Assert.Null(TargetFrameworkSymbols.Infer(null));
		Assert.Null(TargetFrameworkSymbols.Infer([]));
	}

	/// <summary>A project's own conditional symbols must not be mistaken for a framework.</summary>
	[Fact]
	public void Is_not_fooled_by_a_projects_own_symbols()
	{
		var symbols = new[] { "REVIT2024", "INTERNAL_BUILD", "NETFRAMEWORK", "NET48" };

		Assert.Equal("net48", TargetFrameworkSymbols.Infer(symbols));
	}
}
