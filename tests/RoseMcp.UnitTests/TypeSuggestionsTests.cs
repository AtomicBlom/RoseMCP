using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// The near matches named when a location's type is declared by nothing loaded, which is the only
/// thing that tells a typo apart from a module still to load.
/// </summary>
public sealed class TypeSuggestionsTests
{
	private const string Source = """
		namespace Drawboard.Rendering
		{
			public class Win2dGeometryService { public void Build() { } }
			public class GeometryCache { }
			public class Ink { }
		}

		namespace Drawboard.Annotations
		{
			public class AnnotationLayer { }
		}
		""";

	/// <summary>
	/// A difference of case alone is the slip nobody can see in their own typing, and the one that
	/// otherwise reads exactly like waiting for a module.
	/// </summary>
	[Test]
	public void A_name_differing_only_in_case_is_suggested()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		var suggestions = TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.Win2DGeometryService", assembly: null);

		suggestions.ShouldHaveSingleItem().TypeName.ShouldBe("Drawboard.Rendering.Win2dGeometryService");
	}

	[Test]
	public void A_name_a_letter_or_two_out_is_suggested()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.GeometryCahce", assembly: null)
			.ShouldHaveSingleItem().TypeName.ShouldBe("Drawboard.Rendering.GeometryCache");
	}

	/// <summary>The right name in the wrong namespace is as much a miss as a misspelling, and as easy to make.</summary>
	[Test]
	public void The_right_name_in_another_namespace_is_suggested()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.AnnotationLayer", assembly: null)
			.ShouldHaveSingleItem().TypeName.ShouldBe("Drawboard.Annotations.AnnotationLayer");
	}

	/// <summary>
	/// A short name one edit from another is as often a different word as a misspelled one, so only a
	/// difference of case is offered for it.
	/// </summary>
	[Test]
	public void A_short_name_is_matched_only_on_case()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.Inc", assembly: null).ShouldBeEmpty();
		TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.INK", assembly: null).ShouldHaveSingleItem();
	}

	[Test]
	public void Nothing_close_suggests_nothing()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		var suggestions = TypeSuggestions.For([module.ModulePath], "Drawboard.Sync.UploadQueue", assembly: null);

		suggestions.ShouldBeEmpty();
		TypeSuggestions.Clause(suggestions).ShouldBeEmpty();
	}

	/// <summary>
	/// The clause names the module each suggestion is in, because that is where the caller looks next,
	/// and says why the misspelling matters: it will not bind however long it waits.
	/// </summary>
	[Test]
	public void The_clause_names_each_suggestion_and_its_module()
	{
		using var module = CompiledModule.Of(Source, "Rendering");

		var clause = TypeSuggestions.Clause(
			TypeSuggestions.For([module.ModulePath], "Drawboard.Rendering.Win2DGeometryService", assembly: null));

		clause.ShouldContain("did you mean Drawboard.Rendering.Win2dGeometryService (in Rendering.dll)?", Case.Sensitive);
		clause.ShouldContain("never binds", Case.Sensitive);
	}

	/// <summary>A stated assembly limits the suggestions to it, as it limits the bind.</summary>
	[Test]
	public void A_stated_assembly_keeps_suggestions_to_that_module()
	{
		using var rendering = CompiledModule.Of(Source, "Rendering");
		using var other = CompiledModule.Of("namespace Drawboard.Rendering { public class Win2dGeometryService { } }", "Other");

		var suggestions = TypeSuggestions.For(
			[rendering.ModulePath, other.ModulePath], "Drawboard.Rendering.Win2DGeometryService", assembly: "Other");

		suggestions.ShouldHaveSingleItem().ModulePath.ShouldBe(other.ModulePath);
	}
}
