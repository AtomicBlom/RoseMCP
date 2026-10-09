using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// What every test standing a caller in <see cref="NowhereDirectory"/> relies on, checked on whichever
/// OS runs the suite. A shape that is a root on one OS and a relative file name on another fails
/// every such test on the second, each for a reason unrelated to what it asserts, so the shape is
/// pinned here where the failure names it.
/// </summary>
public sealed class NowhereDirectoryTests
{
	/// <summary>Absolute on this OS, and absent, which is the whole of what it promises.</summary>
	[Test]
	public void Nowhere_is_a_fully_qualified_directory_that_does_not_exist()
	{
		var nowhere = NowhereDirectory.Path();

		Path.IsPathFullyQualified(nowhere).ShouldBeTrue($"{nowhere} is relative on this OS, so nothing can be measured from it.");
		Directory.Exists(nowhere).ShouldBeFalse($"{nowhere} exists, so it is somewhere.");
	}

	/// <summary>
	/// A caller standing there has a relative argument measured from there, rather than refused for
	/// lacking a base -- which is what every keyed tool does first, before its routing can answer.
	/// </summary>
	[Test]
	public void A_relative_argument_is_measured_from_nowhere()
	{
		var nowhere = NowhereDirectory.Path();
		var paths = new CallerPaths(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = nowhere }));

		paths.Of("Shop.slnx")!.Value.ShouldBe(Path.Combine(nowhere, "Shop.slnx"));
	}
}
