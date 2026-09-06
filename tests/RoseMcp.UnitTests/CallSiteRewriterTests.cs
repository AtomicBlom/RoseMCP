namespace RoseMcp.UnitTests;

/// <summary>
/// Putting one call site's arguments back for a changed parameter list. The interesting half is
/// what it declines to do, since a plausible rewrite that binds an argument to the wrong parameter
/// is the failure with no symptom.
/// <para>
/// Every case compiles its fixture, because a call site that does not bind is one of the outcomes:
/// an argument written for a parameter the member does not have yet is exactly a call that does not
/// compile, and refusing it is the whole of #59.
/// </para>
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
		var source = """
			public static class Fixture
			{
				public static string Target(string source) => source;

				public static string Use(string source, int span) => Target(source, span);
			}
			""";

		Assert.Null(CallSites.Rewrite(source, "string source, int? within = null", out var refusal));
		Assert.Contains("does not compile as it stands", refusal, StringComparison.Ordinal);
	}

	/// <summary>
	/// The guard that keeps the fix honest. Dropping the argument of a parameter that is going is not
	/// a mistake, it is what removing a parameter means, so refusing every surplus argument outright
	/// would trade one silent wrong answer for a tool that declines the case it exists for.
	/// </summary>
	[Fact]
	public void Still_drops_the_argument_of_a_parameter_that_was_removed()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string name, bool loud) => name;

				public static string Use(string name, bool loud) => Target(name, loud);
			}
			""";

		Assert.Equal("(name)", CallSites.Rewrite(source, "string name"));
	}

	/// <summary>The ordinary case: a call site that says nothing about the new optional is left as it is.</summary>
	[Fact]
	public void Leaves_a_call_site_that_says_nothing_about_the_new_optional()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string source) => source;

				public static string Use(string source) => Target(source);
			}
			""";

		Assert.Equal("(source)", CallSites.Rewrite(source, "string source, int? within = null"));
	}

	/// <summary>
	/// A named argument for a parameter that does not exist yet is the same mistake spelled
	/// differently, and was already refused. Locked in so the positional fix does not route around it.
	/// </summary>
	[Fact]
	public void Refuses_a_named_argument_for_a_parameter_the_old_signature_did_not_have()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string source) => source;

				public static string Use(string source, int span) => Target(source, within: span);
			}
			""";

		Assert.Null(CallSites.Rewrite(source, "string source, int? within = null"));
	}

	/// <summary>
	/// A params parameter legitimately takes more arguments than there are parameters, so an
	/// expansion must not read as arguments with nowhere to go.
	/// </summary>
	[Fact]
	public void Keeps_a_params_expansion_that_runs_past_the_parameter_count()
	{
		var source = """
			public static class Fixture
			{
				public static string Target(string format, params object[] args) => format;

				public static string Use(string format, object a, object b, object c) => Target(format, a, b, c);
			}
			""";

		Assert.Equal("(format, a, b, c)", CallSites.Rewrite(source, "string format, params object[] args"));
	}

	/// <summary>
	/// A named argument has to use the parameter names of the method it is calling, and an override
	/// is free to call its parameters something else than the declaration being changed does. Naming
	/// one from the declaration is CS1739 at every call site reached through such an override --
	/// which is the shape this tool exists to stop rather than to produce.
	/// <para>
	/// The omitted optional in the middle is what forces a name at all: an argument only stays
	/// positional while it would land in its own slot.
	/// </para>
	/// </summary>
	[Fact]
	public void Names_an_argument_after_the_method_the_call_site_binds_to()
	{
		var source = """
			public abstract class Base
			{
				public abstract string Target(string first, string second = "x", string third = "y");
			}

			public sealed class Derived : Base
			{
				public override string Target(string mine, string other = "x", string last = "y") => mine;
			}

			public static class Fixture
			{
				public static string Use(Derived derived, string a, string c) => derived.Target(a, last: c);
			}
			""";

		Assert.Equal(
			"""(a, "-", last: c)""",
			CallSites.Rewrite(
				source,
				"""string first, string separator, string second = "x", string third = "y" """,
				"separator=\"-\""));
	}
}
