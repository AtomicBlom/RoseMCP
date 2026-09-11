using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Finding a method to break in by typing part of its name, across a target's loaded modules.
/// <para>
/// It exists because an agentic session has no IDE in front of it, so a breakpoint is otherwise set
/// by remembering a namespace, a type and a method exactly. What it hands back has to be
/// paste-ready: the location string it carries is what sets the breakpoint, so a match that reads
/// correctly and does not bind is worse than no match at all.
/// </para>
/// </summary>
public sealed class MethodSearchTests
{
	private const string WidgetSource = """
		namespace Probe;

		public static class Widget
		{
			public static int Refresh(int seed) => seed;

			public static int RefreshAll(int seed) => seed + 1;

			public static int Title { get; set; }

			public static int Twice(int seed)
			{
				System.Func<int, int> refreshInner = value => value * 2;
				return refreshInner(seed);
			}
		}

		public static class Refresher
		{
			public static int Run(int seed) => seed;
		}
		""";

	/// <summary>
	/// Best first, and "best" is the method whose name is what was typed. The type whose name merely
	/// contains it comes last, which is the ordering the whole feature turns on.
	/// </summary>
	[Test]
	public void The_method_named_what_was_typed_comes_first()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var found = MethodSearch.Search([fixture.ModulePath], "Refresh", 20);

		Assert.Equal("Probe.Widget", found.Matches[0].TypeName);
		Assert.Equal("Refresh", found.Matches[0].MethodName);
		Assert.Equal("Widget.Refresh", found.Matches[0].DisplayName);
		Assert.Equal("RefreshAll", found.Matches[1].MethodName);
		Assert.Equal("Run", found.Matches[^1].MethodName);
		Assert.Equal(1, found.ModulesSearched);
		Assert.Equal(0, found.ModulesUnreadable);
	}

	/// <summary>
	/// The location is the answer, not the label. It is assembly-qualified so it cannot fail to bind
	/// over the guess the bare form makes about which module a namespace belongs to.
	/// </summary>
	[Test]
	public void A_match_carries_the_location_that_sets_the_breakpoint()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var match = MethodSearch.Search([fixture.ModulePath], "Widget.Refresh", 1).Matches[0];

		Assert.Equal("Probe!Probe.Widget.Refresh", match.Location);
		Assert.Equal("Probe", match.Module);
		Assert.Equal(fixture.ModulePath, match.ModulePath);
		Assert.True(match.HasSymbols);
	}

	/// <summary>A property is one of the things somebody means by "the method or property", under its own name.</summary>
	[Test]
	public void An_accessor_is_found_by_the_property_name()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var found = MethodSearch.Search([fixture.ModulePath], "Title", 20);

		Assert.Contains(found.Matches, match => match.DisplayName == "Widget.Title (get)");
		Assert.Contains(found.Matches, match => match.DisplayName == "Widget.Title (set)");
	}

	/// <summary>
	/// A lambda body is a method with a name nobody types, so it is not in a name search. It is
	/// reached by picking a line, which is the only way anybody would look for it.
	/// </summary>
	[Test]
	public void A_compiler_generated_method_is_not_offered_by_name()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var found = MethodSearch.Search([fixture.ModulePath], "refreshInner", 20);
		Assert.Empty(found.Matches);

		// And the containing method is, so the absence above is the filter rather than the module.
		Assert.Contains(MethodSearch.Search([fixture.ModulePath], "Twice", 20).Matches, match => match.MethodName == "Twice");
	}

	/// <summary>
	/// Whether a position can be picked inside a method is a fact about the match, not a reason to
	/// leave it out: a method with no symbols is still worth breaking at its first instruction.
	/// </summary>
	[Test]
	public void A_module_without_symbols_is_searched_and_says_so()
	{
		using var fixture = CompiledModule.Of(WidgetSource, "Probe", withSymbols: false);

		var match = MethodSearch.Search([fixture.ModulePath], "Refresh", 1).Matches[0];

		Assert.Equal("Refresh", match.MethodName);
		Assert.False(match.HasSymbols);
	}

	/// <summary>
	/// The limit takes the top of the whole answer rather than the first modules read, and the total
	/// says the list is the top of something.
	/// </summary>
	[Test]
	public void The_limit_cuts_a_ranked_answer_and_the_total_says_so()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var found = MethodSearch.Search([fixture.ModulePath], "Refresh", 1);

		Assert.Equal("Refresh", Assert.Single(found.Matches).MethodName);
		Assert.True(found.Total > 1, $"more than one method matched, and {found.Total} were counted");
	}

	/// <summary>
	/// A target loads native libraries and builds modules in memory, so paths that cannot be read are
	/// an ordinary number rather than a failure of the search.
	/// </summary>
	[Test]
	public void A_module_that_cannot_be_read_is_counted_rather_than_thrown()
	{
		using var fixture = CompiledModule.Of(WidgetSource);
		var missing = Path.Combine(Path.GetTempPath(), $"rose-absent-{Guid.NewGuid():n}.dll");

		var found = MethodSearch.Search([missing, fixture.ModulePath], "Refresh", 20);

		Assert.Equal(1, found.ModulesSearched);
		Assert.Equal(1, found.ModulesUnreadable);
		Assert.NotEmpty(found.Matches);
	}

	/// <summary>
	/// Too short a query is not run at all. One character matches most of a framework, and an
	/// autocomplete that answers it has read every loaded module to hand back nothing usable.
	/// </summary>
	[Test]
	public void Too_short_a_query_reads_nothing()
	{
		using var fixture = CompiledModule.Of(WidgetSource);

		var found = MethodSearch.Search([fixture.ModulePath], "R", 20);

		Assert.Empty(found.Matches);
		Assert.Equal(0, found.ModulesSearched);
	}
}
