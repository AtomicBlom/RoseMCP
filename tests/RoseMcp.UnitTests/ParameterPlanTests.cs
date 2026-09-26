using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// Working out what changed between two parameter lists, which everything else hangs off. Matched
/// by name, because a name is the only thing about a parameter a caller can be relied on to keep.
/// </summary>
public sealed class ParameterPlanTests
{
	/// <summary>
	/// The common case, and the one worth naming: an optional flag on the end. Nothing at any call
	/// site has to change, which is why the tool can report them all and touch none.
	/// </summary>
	[Test]
	public void Knows_that_an_optional_parameter_on_the_end_leaves_call_sites_alone()
	{
		var plan = Plan("string name", "string name, bool loud = false");

		plan.CallSitesUnaffected.ShouldBeTrue();
		plan.Removed.ShouldBeEmpty();
		plan.Added.Select(parameter => parameter.Name).ShouldBe(["loud"]);
	}

	[Test]
	public void Knows_that_a_required_parameter_does_not()
	{
		Plan("string name", "string name, bool loud").CallSitesUnaffected.ShouldBeFalse();
	}

	/// <summary>
	/// A new parameter in the middle moves everything after it, so the arguments after it can no
	/// longer be positional -- which is a call-site change even though nothing was removed.
	/// </summary>
	[Test]
	public void Knows_that_a_parameter_inserted_in_the_middle_moves_the_rest()
	{
		var plan = Plan("string title, string name", "string title, bool loud = false, string name");

		plan.CallSitesUnaffected.ShouldBeFalse("a parameter inserted in the middle moves the call sites");
		plan.WhyImpossible().ShouldBeNull();
	}

	[Test]
	public void Names_what_was_removed()
	{
		var plan = Plan("string name, bool loud", "string name");

		plan.Removed.ShouldBe(["loud"]);
		plan.CallSitesUnaffected.ShouldBeFalse("a removed parameter moves the call sites");
	}

	/// <summary>
	/// A parameter that kept its name and changed type is the same parameter, and the call sites go
	/// on passing what they passed -- which is why it is reported rather than assumed harmless.
	/// </summary>
	[Test]
	public void Names_what_changed_type_without_treating_it_as_new()
	{
		var plan = Plan("string name", "object name");

		plan.Retyped.ShouldBe(["name"]);
		plan.Added.ShouldBeEmpty();
		plan.Removed.ShouldBeEmpty();
	}

	/// <summary>
	/// Renaming a parameter reads as removing one and adding another, which is the right answer:
	/// rose_rename_symbol moves the named arguments at every call site too, and nothing here would.
	/// </summary>
	[Test]
	public void Reads_a_rename_as_a_removal_and_an_addition()
	{
		var plan = Plan("string name", "string other");

		plan.Removed.ShouldBe(["name"]);
		plan.Added.Select(parameter => parameter.Name).ShouldBe(["other"]);
	}

	[Test]
	public void Refuses_to_swap_two_parameters_that_already_exist()
	{
		var refusal = Plan("string title, string name", "string name, string title").WhyImpossible();

		refusal.ShouldNotBeNull();
		refusal.ShouldContain("would move in front of", Case.Sensitive);
	}

	/// <summary>Emptying the list is a removal of everything, not an impossibility.</summary>
	[Test]
	public void Takes_an_empty_list_as_removing_them_all()
	{
		var plan = Plan("string name, bool loud", string.Empty);

		plan.Removed.ShouldBe(["name", "loud"]);
		plan.Parameters.ShouldBeEmpty();
		plan.WhyImpossible().ShouldBeNull();
	}

	private static ParameterPlan Plan(string existing, string wanted) =>
		ParameterPlan.For(Parse(existing), Parse(wanted));

	private static SeparatedSyntaxList<ParameterSyntax> Parse(string text) =>
		MemberSyntax.ParseParameters(text, null);
}
