using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which CI job runs which tests, decided by what each one needs rather than by which suite it is in.
/// <para>
/// A probe-app test wants a C++ toolset, the Windows App SDK, developer mode and a machine-wide
/// package registration, so it runs in a job of its own whose runner is set up for exactly those, and
/// the plain integration job excludes it. Everything else in the live-app half drives
/// <c>DebugProbeTarget</c>, an ordinary .NET child process, which any hosted Windows runner serves.
/// Both jobs set <see cref="MachineLimit.RequiredVariable"/>, so a missing toolchain in either is a
/// failure rather than a skip that reads as a pass.
/// </para>
/// <para>
/// Decided from the fixture rather than remembered, because a category applied by hand drifts in
/// both directions and neither direction shows up as a failure. A probe-app test without it is a
/// red build on a runner that was never set up to serve it. A debugger test wearing it is moved to
/// the slower job for nothing, and runs only when that job's paths say so.
/// </para>
/// </summary>
public sealed class ProbeAppCategoryTests
{
	/// <summary>
	/// The category the plain integration job excludes and the probe-app job runs. Spelled once here
	/// and read from <c>.github/workflows/ci.yml</c> by the tests below, so the two cannot drift.
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
					+ "runs it in the plain integration job, on a runner nothing set up to serve it.");
		}
	}

	/// <summary>
	/// And nothing else carries it. A debugger test wearing it leaves the job that runs on every change
	/// for one that runs only when the probe apps' inputs change, which is coverage given up quietly --
	/// the direction that costs the most and shows the least.
	/// </summary>
	[Test]
	public void Nothing_that_runs_on_a_plain_dotnet_process_is_excluded()
	{
		foreach (var type in TestClasses().Where(type => !NeedsProbeApp(type)))
		{
			Categories(type).Contains(Excluded).ShouldBeFalse(
				$"{type.Name} drives a plain .NET process and is excluded from the plain integration job anyway. Take the "
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
					$"{type.Name}.{method.Name} is excluded from the plain integration job on a class that needs no probe app.");
			}
		}
	}

	/// <summary>
	/// And that the plain integration job excludes the category this file is about. The filter is a
	/// string in a YAML file and the attribute is a string in C#; nothing but this connects them, and a
	/// rename that updates one silently runs the whole suite or none of it.
	/// </summary>
	[Test]
	public void The_workflow_excludes_that_category_and_no_other()
	{
		var workflow = File.ReadAllText(
			Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

		workflow.ShouldContain($"[Category!={Excluded}]", Case.Sensitive);
		workflow.ShouldNotContain("[Category!=LiveApp]", Case.Sensitive);
	}

	/// <summary>
	/// The category is excluded from one job only because another runs it, on a runner set up for it,
	/// and both jobs say a skip is a failure. Without the second half, a runner that lost a component
	/// would skip the probe-app tests and the job would be as green as one that ran them all.
	/// </summary>
	[Test]
	public void The_workflow_runs_that_category_in_one_job_and_neither_side_may_skip()
	{
		var jobs = WorkflowJobs(File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml")));

		var running = jobs.Where(job => job.Value.Contains($"[Category={Excluded}]", StringComparison.Ordinal)).ToList();
		running.Count.ShouldBe(1, $"exactly one job should run [Category={Excluded}]; found {string.Join(", ", running.Select(job => job.Key))}");

		var excluding = jobs.Where(job => job.Value.Contains($"[Category!={Excluded}]", StringComparison.Ordinal)).ToList();
		excluding.Count.ShouldBe(1, $"exactly one job should exclude [Category!={Excluded}]; found {string.Join(", ", excluding.Select(job => job.Key))}");

		foreach (var (name, text) in running.Concat(excluding))
		{
			SetsRequired(text).ShouldBeTrue(
				$"The {name} job runs integration tests without setting {MachineLimit.RequiredVariable}: '1', so a "
					+ "missing toolchain there skips, and a skip reads as a pass.");
		}
	}

	/// <summary>
	/// Every skip in this suite asks <see cref="MachineLimit"/> first. A test that calls the framework's
	/// skip itself is one the CI switch cannot reach, so it would go back to reading as a pass on the
	/// one machine that is supposed to have everything.
	/// </summary>
	[Test]
	public void No_test_skips_without_asking_whether_the_machine_should_have_had_it()
	{
		var source = Path.Combine(RepositoryRoot(), "tests", "RoseMcp.IntegrationTests");
		var direct = new Regex(@"\bSkip\.\w+\(|\[Skip\(");

		var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
			.Where(file => !IsBuildOutput(source, file))
			.Where(file => Path.GetFileName(file) != $"{nameof(MachineLimit)}.cs")
			.SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, number: index + 1)))
			.Where(site => direct.IsMatch(site.line))
			.Select(site => $"{Path.GetRelativePath(source, site.file)}:{site.number}")
			.ToList();

		offenders.ShouldBeEmpty(
			$"These skip without going through {nameof(MachineLimit)}.{nameof(MachineLimit.Reached)}, so CI's "
				+ $"{MachineLimit.RequiredVariable} cannot turn them into failures: {string.Join(", ", offenders)}");
	}

	/// <summary>
	/// The workflow's jobs by name, each with its own text. A job starts at a two-space-indented key
	/// under <c>jobs:</c> and runs to the next one; that is all the YAML this needs to read, and a
	/// parser would be a package reference for one test.
	/// </summary>
	private static Dictionary<string, string> WorkflowJobs(string workflow)
	{
		var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
		var header = new Regex(@"^  ([A-Za-z0-9_-]+):\s*$");
		var inJobs = false;
		string? current = null;
		var text = new StringBuilder();

		foreach (var line in workflow.Split('\n').Select(line => line.TrimEnd('\r')))
		{
			if (!inJobs)
			{
				inJobs = line == "jobs:";
				continue;
			}

			var match = header.Match(line);
			if (match.Success)
			{
				if (current is not null) jobs[current] = text.ToString();
				current = match.Groups[1].Value;
				text.Clear();
				continue;
			}

			text.AppendLine(line);
		}

		if (current is not null) jobs[current] = text.ToString();
		return jobs;
	}

	/// <summary>Whether a job's text sets the switch on, as an <c>env</c> entry.</summary>
	private static bool SetsRequired(string job) =>
		Regex.IsMatch(job, $@"^\s+{MachineLimit.RequiredVariable}:\s*'1'\s*$", RegexOptions.Multiline);

	/// <summary>Whether a file is under the project's <c>bin</c> or <c>obj</c>, which hold copies rather than source.</summary>
	private static bool IsBuildOutput(string projectDirectory, string file)
	{
		var first = Path.GetRelativePath(projectDirectory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
		return first is "bin" or "obj";
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
