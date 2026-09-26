using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The gate on a conditional breakpoint. It decides whether a hit stops the target, so both of its
/// failures are silent: a condition that never holds stops nowhere and reads as a breakpoint that
/// did not bind, and one that always holds stops on every hit and reads as a condition that was
/// ignored. Neither says anything, which is why the matrix is here rather than left to a test that
/// has to drive a real debuggee to reach one operator.
/// </summary>
public sealed class BreakpointConditionTests
{
	private static LiveVariable Local(string name, string? value) =>
		new() { Name = name, Kind = "local", Value = value, Path = "local:0", HasChildren = false };

	private static bool Holds(string condition, string name, string value) =>
		BreakpointCondition.Parse(condition)!.Evaluate([Local(name, value)]);

	/// <summary>
	/// Two-character operators are matched before one-character ones, and that ordering is the whole
	/// of it: <c>&lt;</c> occurs inside <c>&lt;=</c>, so matching the short one first would split
	/// "iteration &lt;= 5" into the variable "iteration" and the literal "= 5", which parses as no
	/// number and falls through to a string compare that is false forever.
	/// </summary>
	[Test]
	[Arguments("iteration <= 5", "iteration", "<=", "5")]
	[Arguments("iteration >= 5", "iteration", ">=", "5")]
	[Arguments("iteration == 5", "iteration", "==", "5")]
	[Arguments("iteration != 5", "iteration", "!=", "5")]
	[Arguments("iteration < 5", "iteration", "<", "5")]
	[Arguments("iteration > 5", "iteration", ">", "5")]
	public void Matches_the_longest_operator_first(string text, string variable, string op, string literal)
	{
		var condition = BreakpointCondition.Parse(text);

		condition.ShouldNotBeNull();
		condition!.Variable.ShouldBe(variable);
		condition.Operator.ShouldBe(op);
		condition.Literal.ShouldBe(literal);
	}

	[Test]
	[Arguments("iteration == 30", "30", true)]
	[Arguments("iteration == 30", "29", false)]
	[Arguments("iteration != 30", "29", true)]
	[Arguments("iteration != 30", "30", false)]
	[Arguments("iteration < 30", "29", true)]
	[Arguments("iteration < 30", "30", false)]
	[Arguments("iteration <= 30", "30", true)]
	[Arguments("iteration <= 30", "31", false)]
	[Arguments("iteration > 30", "31", true)]
	[Arguments("iteration > 30", "30", false)]
	[Arguments("iteration >= 30", "30", true)]
	[Arguments("iteration >= 30", "29", false)]
	public void Compares_numbers_with_every_operator(string condition, string value, bool expected)
		=> Holds(condition, "iteration", value).ShouldBe(expected);

	/// <summary>
	/// Numbers are compared as numbers, not as the text the debugger handed back. A string compare
	/// would make 9 greater than 30 and gate out exactly the hits the caller asked to stop on.
	/// </summary>
	[Test]
	public void Compares_numbers_as_numbers_rather_than_text()
	{
		Holds("iteration > 9", "iteration", "30").ShouldBeTrue();
		Holds("iteration > 30", "iteration", "9").ShouldBeFalse();
	}

	[Test]
	[Arguments("ready == true", "true", true)]
	[Arguments("ready == true", "false", false)]
	[Arguments("ready != true", "false", true)]
	public void Compares_booleans_by_value(string condition, string value, bool expected)
		=> Holds(condition, "ready", value).ShouldBe(expected);

	/// <summary>
	/// An ordering on a bool has no meaning, so it does not hold rather than guessing at one. It is
	/// false in both directions on purpose: a caller who writes it gets a breakpoint that never
	/// fires, which is visible, rather than one that fires on a coin toss.
	/// </summary>
	[Test]
	public void Refuses_to_order_booleans()
	{
		Holds("ready > false", "ready", "true").ShouldBeFalse();
		Holds("ready < true", "ready", "false").ShouldBeFalse();
	}

	/// <summary>
	/// Strings compare by equality only, and one layer of quotes comes off either side, so the
	/// condition can be written the way it would be written in source.
	/// </summary>
	[Test]
	[Arguments("label == \"beat\"", "beat", true)]
	[Arguments("label == beat", "\"beat\"", true)]
	[Arguments("label == \"beat\"", "tick", false)]
	[Arguments("label != \"beat\"", "tick", true)]
	public void Compares_strings_by_equality_through_one_layer_of_quotes(string condition, string value, bool expected)
		=> Holds(condition, "label", value).ShouldBe(expected);

	[Test]
	public void Compares_strings_case_sensitively()
		=> Holds("label == beat", "label", "Beat").ShouldBeFalse();

	/// <summary>
	/// A variable the frame does not have evaluates false, so the breakpoint does not fire. The
	/// alternative is stopping on every hit of a condition naming a misspelled local, which is the
	/// behaviour a caller was trying to avoid by writing a condition at all.
	/// </summary>
	[Test]
	public void Does_not_hold_when_the_frame_has_no_such_variable()
	{
		Holds("missing == 1", "iteration", "1").ShouldBeFalse();
		BreakpointCondition.Parse("iteration == 1")!.Evaluate([]).ShouldBeFalse();
	}

	[Test]
	public void Does_not_hold_when_the_variable_has_no_value()
		=> BreakpointCondition.Parse("iteration == 1")!
			.Evaluate([Local("iteration", null)]).ShouldBeFalse();

	/// <summary>No condition is not a malformed one: an unconditional breakpoint stops on every hit.</summary>
	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments("   ")]
	public void Reads_nothing_as_no_condition(string? text)
		=> BreakpointCondition.Parse(text).ShouldBeNull();

	/// <summary>
	/// A condition that cannot be parsed is refused at the point it is set, rather than accepted and
	/// silently never held -- the failure a caller cannot tell from a breakpoint that did not bind.
	/// </summary>
	[Test]
	[Arguments("iteration")]
	[Arguments("iteration ==")]
	[Arguments("== 5")]
	[Arguments("iteration = 5")]
	public void Refuses_a_condition_it_cannot_parse(string text)
	{
		var failure = Should.Throw<ArgumentException>(() => BreakpointCondition.Parse(text)).ShouldBeOfType<ArgumentException>();

		failure.Message.ShouldContain(text, Case.Sensitive);
	}

	[Test]
	public void Ignores_whitespace_around_both_sides()
	{
		var condition = BreakpointCondition.Parse("  iteration   >=   30  ");

		condition!.Variable.ShouldBe("iteration");
		condition.Literal.ShouldBe("30");
	}
}
