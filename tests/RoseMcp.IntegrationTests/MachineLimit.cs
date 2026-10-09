using System.Diagnostics.CodeAnalysis;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a test does when this machine cannot give it what it needs: a skip on a machine that may
/// legitimately lack it, and a failure on one that is supposed to have it.
/// <para>
/// A skip reads as a pass. On a developer machine without the C++ toolset or the Windows App SDK
/// that is the right answer, because the suite is about the change and not about the machine. On a
/// CI runner provisioned for these tests it is the wrong one, because a runner whose setup quietly
/// lost a component would report the same green as one that ran everything, and nobody reads the
/// skip count of a green job. So the machine says which it is, by setting
/// <see cref="RequiredVariable"/>, and every skip in this suite goes through here to ask.
/// </para>
/// <para>
/// One switch for every kind of limit rather than one per toolchain, because what a job sets it to
/// mean is "this runner was set up for everything its tests need" -- and a job that was not set up
/// for something has excluded those tests by category instead, which says so out loud.
/// <c>ProbeAppCategoryTests</c> holds both ends: no skip bypasses this, and the jobs set it.
/// </para>
/// </summary>
internal static class MachineLimit
{
	/// <summary>
	/// The environment variable that turns every skip in this suite into a failure. Set to <c>1</c>
	/// (or <c>true</c>) by the CI jobs; unset, a missing toolchain skips as it always does.
	/// </summary>
	internal const string RequiredVariable = "ROSEMCP_TESTS_REQUIRE_TOOLCHAIN";

	/// <summary>
	/// Skips the calling test for a reason that is a fact about this machine, or fails it naming that
	/// reason where <see cref="RequiredVariable"/> says the machine was meant to have it.
	/// </summary>
	/// <param name="reason">What the machine lacks, said the same way whichever happens.</param>
	[DoesNotReturn]
	internal static void Reached(string reason) =>
		Reached(reason, Environment.GetEnvironmentVariable(RequiredVariable));

	/// <summary>
	/// <see cref="Reached(string)"/> with the switch passed in, so the rule can be tested without
	/// setting an environment variable under tests that run in parallel.
	/// </summary>
	/// <param name="reason">What the machine lacks.</param>
	/// <param name="required">The value of <see cref="RequiredVariable"/>, or null where it is unset.</param>
	[DoesNotReturn]
	internal static void Reached(string reason, string? required)
	{
		if (IsRequired(required))
		{
			throw new InvalidOperationException(
				$"{reason} {RequiredVariable} is set, which says this machine was set up to have everything "
					+ "these tests need, so this is a failure rather than a skip: whatever set the machine up "
					+ "did not do what it said.");
		}

		Skip.Test(reason);

		// Skip.Test throws, and the compiler cannot know that from an attribute the framework does not
		// carry. Marking this method as not returning is what lets the callers read as guards.
		throw new InvalidOperationException(reason);
	}

	/// <summary>
	/// Whether a value of <see cref="RequiredVariable"/> turns the switch on: <c>1</c> or <c>true</c>,
	/// in any case. Anything else, an empty value included, leaves skips as skips -- a switch that is
	/// on by accident would turn a developer's run red over a toolchain they never meant to install.
	/// </summary>
	internal static bool IsRequired(string? value) =>
		value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
