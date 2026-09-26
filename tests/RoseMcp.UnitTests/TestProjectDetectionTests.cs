
namespace RoseMcp.UnitTests;

/// <summary>
/// That the test-framework list recognises the framework this repository's own tests run on.
/// </summary>
/// <remarks>
/// Asked of the assemblies beside the running test rather than of the list itself, because a list
/// checked against its own contents cannot fail. Changing the runner left every test project here
/// unrecognised and nothing noticed, since the only reader of the list was the list's own author --
/// so the assertion has to come from outside it, and the directory this test is executing from is a
/// real test project's output by construction.
/// </remarks>
public sealed class TestProjectDetectionTests
{
	[Test]
	public void Recognises_the_framework_this_test_is_running_on()
	{
		var beside = Path.GetDirectoryName(typeof(TestProjectDetectionTests).Assembly.Location);
		string.IsNullOrEmpty(beside).ShouldBeFalse("the running test assembly should have a location on disk");

		var assemblies = Directory.EnumerateFiles(beside!, "*.dll")
			.Select(Path.GetFileNameWithoutExtension)
			.Where(name => !string.IsNullOrEmpty(name))
			.Select(name => name!)
			.ToList();

		var recognised = assemblies.Where(TestProjects.Recognises).ToList();

		recognised.Count.ShouldBeGreaterThan(0,
			"no assembly beside this test is on TestProjects.Frameworks, so Rose would report its own test "
				+ "projects as product code. Add the framework's assembly name to that list. Beside it: "
				+ string.Join(", ", assemblies.Where(name => name.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("nunit", StringComparison.OrdinalIgnoreCase))));
	}

	[Test]
	public void Does_not_mistake_an_ordinary_assembly_for_a_framework()
	{
		TestProjects.Recognises("RoseMcp.Broker").ShouldBeFalse();
		TestProjects.Recognises("System.Text.Json").ShouldBeFalse();

		// Prefix-adjacent, so an accidental StartsWith would be caught here.
		TestProjects.Recognises("TUnit.Core.Extras").ShouldBeFalse();
		TestProjects.Recognises("xunit.v3.assert.extras").ShouldBeFalse();
	}
}
