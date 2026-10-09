using Microsoft.CodeAnalysis;

namespace RoseMcp.UnitTests;

/// <summary>
/// Every kind a name search can answer with is a kind it can be narrowed to, because a capped search
/// groups its matches by kind and says each group is a value <c>kind</c> takes.
/// </summary>
public sealed class SearchKindTests
{
	[Test]
	[Arguments(SymbolKind.NamedType)]
	[Arguments(SymbolKind.Method)]
	[Arguments(SymbolKind.Property)]
	[Arguments(SymbolKind.Field)]
	[Arguments(SymbolKind.Event)]
	[Arguments(SymbolKind.Namespace)]
	public void Takes_every_kind_a_match_can_carry_as_the_match_spells_it(SymbolKind kind)
	{
		NavigationService.SearchKind(kind.ToString()).ShouldBe(kind.ToString());
	}

	[Test]
	public void Refuses_a_kind_no_match_carries_naming_namespace_among_the_ones_there_are()
	{
		Should.Throw<ArgumentException>(() => NavigationService.SearchKind("Class")).Message.ShouldContain("Namespace", Case.Sensitive);
	}
}
