using System.Reflection;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which tests CI can run, decided by what each one needs rather than by which suite it is in.
/// <para>
/// A probe-app test wants a C++ toolset, the Windows App SDK, developer mode and a machine-wide
/// package registration, and answers their absence by skipping -- which reads as a pass. So the
/// category excludes exactly those, and the name says what it is about: everything else in the
/// live-app half drives <c>DebugProbeTarget</c>, an ordinary .NET child process, and a hosted
/// Windows runner serves it perfectly well.
/// </para>
/// <para>
/// Decided from the fixture rather than remembered, because a category applied by hand drifts in
/// both directions and neither direction shows up as a failure. A probe-app test without it is a
/// red build on a runner that could never have served it. A debugger test wearing it is coverage
/// nobody knows they have lost -- and that one is silent by construction, since the evidence for it
/// is a test that does not run.
/// </para>
/// </summary>
public sealed class ProbeAppCategoryTests
{
	/// <summary>
	/// The category CI excludes. Spelled once here and read from
	/// <c>.github/workflows/ci.yml</c> by the test below, so the two cannot drift.
	/// </summary>
	private const string Excluded = "ProbeApp";

	/// <summary>
	/// A class that takes a probe app needs everything a probe app needs, so it carries the
	/// category. The fixture in its constructor is the fact that decides it -- there is no way to
	/// reach one of those apps without asking for it by type.
	/// </summary>
	[Test]
	public void Every_class_that_takes_a_probe_app_is_excluded()
	{
		foreach (var type in TestClasses().Where(NeedsProbeApp))
		{
			Categories(type).Contains(Excluded).ShouldBeTrue(
				$"{type.Name} takes a probe app, so it needs [Category(\"{Excluded}\")]: without it CI "
					+ "runs it on a runner that has no toolchain to serve it.");
		}
	}

	/// <summary>
	/// And nothing else carries it. A debugger test marked excluded is coverage given up silently,
	/// which is the direction that costs the most and shows the least.
	/// </summary>
	[Test]
	public void Nothing_that_runs_on_a_plain_dotnet_process_is_excluded()
	{
		foreach (var type in TestClasses().Where(type => !NeedsProbeApp(type)))
		{
			Categories(type).Contains(Excluded).ShouldBeFalse(
				$"{type.Name} drives a plain .NET process and is excluded from CI anyway. Take the "
					+ $"[Category(\"{Excluded}\")] off, or give it the probe app it apparently needs.");
		}
	}

	/// <summary>
	/// A method may carry the category too, for a class that is otherwise ordinary, and the same
	/// rule applies to it: excluding one needs a reason a runner cannot supply.
	/// </summary>
	[Test]
	public void No_method_is_excluded_without_its_class_needing_a_probe_app()
	{
		foreach (var type in TestClasses().Where(type => !NeedsProbeApp(type)))
		{
			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				method.GetCustomAttributes<CategoryAttribute>().Any(category => category.Category == Excluded).ShouldBeFalse(
					$"{type.Name}.{method.Name} is excluded from CI on a class that needs no probe app.");
			}
		}
	}

	/// <summary>
	/// And that CI excludes the category this file is about. The filter is a string in a YAML file
	/// and the attribute is a string in C#; nothing but this connects them, and a rename that
	/// updates one silently runs the whole suite or none of it.
	/// </summary>
	[Test]
	public void The_workflow_excludes_that_category_and_no_other()
	{
		var workflow = File.ReadAllText(
			Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

		workflow.ShouldContain($"[Category!={Excluded}]", Case.Sensitive);
		workflow.ShouldNotContain("[Category!=LiveApp]", Case.Sensitive);
	}

	/// <summary>Every test class in this assembly: anything declaring a <c>[Test]</c>.</summary>
	private static IEnumerable<Type> TestClasses() =>
		typeof(ProbeAppCategoryTests).Assembly
			.GetTypes()
			.Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
				.Any(method => method.GetCustomAttribute<TestAttribute>() is not null));

	/// <summary>
	/// Whether a class asks for one of the XAML probe apps. They are handed to a class through its
	/// constructor by <c>ClassDataSource</c>, so a parameter of that shape is the whole signal --
	/// and the naming convention is what a fourth probe app has to follow to be seen here.
	/// </summary>
	private static bool NeedsProbeApp(Type type) =>
		type.GetConstructors()
			.SelectMany(constructor => constructor.GetParameters())
			.Any(parameter => parameter.ParameterType.Name.EndsWith("ProbeApp", StringComparison.Ordinal));

	private static IReadOnlyList<string> Categories(Type type) =>
		[.. type.GetCustomAttributes<CategoryAttribute>().Select(category => category.Category)];

	private static string RepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx"))) return directory.FullName;
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
