using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// Putting one call site's arguments back for a changed parameter list. Pure syntax in, syntax or a
/// refusal out, so it is a unit test -- and the interesting half is what it declines to do, since a
/// plausible rewrite that binds an argument to the wrong parameter is the failure with no symptom.
/// </summary>
public sealed class CallSiteRewriterTests
{
	/// <summary>
	/// Issue #59. A call site written in anticipation of the parameter being added -- which does not
	/// compile at that moment, and is exactly why the tool is being run. The surplus argument landed
	/// in a slot no new parameter claimed and was dropped without a word, leaving a call that
	/// compiles and means something else: the test it came from went on passing for a reason
	/// unrelated to what it was written to check.
	/// </summary>
	[Fact]
	public void Refuses_a_call_site_that_already_wrote_an_argument_for_the_parameter_being_added()
	{
		Assert.Null(Rewrite("Disagreeing(source, span)", "string source", "string source, TextSpan? within = null"));
	}

	/// <summary>
	/// The guard that keeps the fix honest. Dropping the argument of a parameter that is going is not
	/// a mistake, it is what removing a parameter means, so refusing every surplus argument outright
	/// would trade one silent wrong answer for a tool that declines the case it exists for.
	/// </summary>
	[Fact]
	public void Still_drops_the_argument_of_a_parameter_that_was_removed()
	{
		var rewritten = Rewrite("Say(name, loud)", "string name, bool loud", "string name");

		Assert.NotNull(rewritten);
		Assert.Equal("(name)", rewritten!.ToString());
	}

	/// <summary>The ordinary case: a call site that says nothing about the new optional is left as it is.</summary>
	[Fact]
	public void Leaves_a_call_site_that_says_nothing_about_the_new_optional()
	{
		var rewritten = Rewrite("Disagreeing(source)", "string source", "string source, TextSpan? within = null");

		Assert.NotNull(rewritten);
		Assert.Equal("(source)", rewritten!.ToString());
	}

	/// <summary>
	/// A named argument for a parameter that does not exist yet is the same mistake spelled
	/// differently, and was already refused. Locked in so the positional fix does not route around it.
	/// </summary>
	[Fact]
	public void Refuses_a_named_argument_for_a_parameter_the_old_signature_did_not_have()
	{
		Assert.Null(Rewrite("Disagreeing(source, within: span)", "string source", "string source, TextSpan? within = null"));
	}

	/// <summary>
	/// A params parameter legitimately takes more arguments than there are parameters, so the surplus
	/// check must not read an expansion as an argument with nowhere to go.
	/// </summary>
	[Fact]
	public void Keeps_a_params_expansion_that_runs_past_the_parameter_count()
	{
		var rewritten = Rewrite("Log(format, a, b, c)", "string format, params object[] args", "string format, params object[] args");

		Assert.NotNull(rewritten);
		Assert.Equal("(format, a, b, c)", rewritten!.ToString());
	}

	private static ArgumentListSyntax? Rewrite(string call, string existing, string wanted)
	{
		var plan = ParameterPlan.For(Parse(existing), Parse(wanted));
		var invocation = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(call);

		return CallSiteRewriter.Rewrite(invocation.ArgumentList, plan, new Dictionary<string, string>(), skip: 0);
	}

	private static SeparatedSyntaxList<ParameterSyntax> Parse(string text) =>
		MemberSyntax.ParseParameters(text, null);
}
