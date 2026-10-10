using TUnit.Core.Exceptions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The switch that decides whether a missing toolchain skips a test or fails it. Here rather than in
/// the unit suite because the rule belongs to this suite's own tests and nothing else reads it; it
/// starts no process and loads no fixture.
/// </summary>
public sealed class MachineLimitTests
{
	/// <summary>
	/// A machine that says it has everything gets a failure that names both what was missing and the
	/// switch that made it a failure, so the person reading a red CI job knows it is the runner's
	/// setup to look at rather than the code.
	/// </summary>
	[Test]
	public void A_limit_fails_the_test_where_the_machine_was_meant_to_have_everything()
	{
		var failure = Should.Throw<InvalidOperationException>(() => MachineLimit.Reached("No C++ toolset.", "1"));

		failure.Message.ShouldStartWith("No C++ toolset.");
		failure.Message.ShouldContain(MachineLimit.RequiredVariable);
	}

	/// <summary>Unset, a limit skips exactly as it always has, which is what a developer machine relies on.</summary>
	[Test]
	public void A_limit_skips_the_test_where_nothing_says_otherwise()
	{
		var skipped = Should.Throw<SkipTestException>(() => MachineLimit.Reached("No C++ toolset.", null));

		skipped.Reason.ShouldBe("No C++ toolset.");
	}

	/// <summary>
	/// Only an unmistakable yes turns the switch on. A value that merely looks set is not one, because
	/// a switch on by accident turns a developer's run red over a toolchain they never meant to have.
	/// </summary>
	[Test]
	[Arguments("1", true)]
	[Arguments("true", true)]
	[Arguments("True", true)]
	[Arguments(" 1 ", true)]
	[Arguments("0", false)]
	[Arguments("false", false)]
	[Arguments("yes", false)]
	[Arguments("", false)]
	[Arguments(null, false)]
	public void The_switch_is_on_only_for_one_or_true(string? value, bool required)
	{
		MachineLimit.IsRequired(value).ShouldBe(required);
	}
}
