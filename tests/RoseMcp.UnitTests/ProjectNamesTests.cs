

using Microsoft.CodeAnalysis;

namespace RoseMcp.UnitTests;

/// <summary>
/// Turning the project a caller named into projects, the one way every tool does it, and the rule
/// that no tool does it another way.
/// </summary>
public sealed class ProjectNamesTests
{
	[Test]
	public void Finds_a_project_by_name_whatever_its_case()
	{
		using var workspace = Workspace(out var solution, "Core", "Tests");

		ProjectNames.Resolve(solution, "core").Select(project => project.Name).ShouldBe(["Core"]);
	}

	/// <summary>
	/// A multi-targeted project is a Roslyn project per framework, and a caller naming the project
	/// means all of them, the same as the path to its project file does.
	/// </summary>
	[Test]
	public void Finds_every_framework_of_a_multi_targeted_project()
	{
		using var workspace = Workspace(out var solution, "Core(net8.0)", "Core(net10.0)", "CoreTests");

		ProjectNames.Resolve(solution, "Core").Select(project => project.Name).Order(StringComparer.Ordinal)
			.ShouldBe(["Core(net10.0)", "Core(net8.0)"]);
	}

	[Test]
	public void Finds_a_project_by_the_path_to_its_project_file()
	{
		using var workspace = Workspace(out var solution, "Core", "Tests");

		var path = solution.Projects.Single(project => project.Name == "Tests").FilePath!;

		ProjectNames.Resolve(solution, path).Select(project => project.Name).ShouldBe(["Tests"]);
	}

	/// <summary>
	/// Refused, never widened and never emptied. Both the wide answer and the empty one come back
	/// well-formed, and either reads as an answer about the project the caller mistyped.
	/// </summary>
	[Test]
	public void Refuses_a_name_no_project_carries_and_names_the_ones_there_are()
	{
		using var workspace = Workspace(out var solution, "Core", "Tests");

		var refusal = Should.Throw<ArgumentException>(() => ProjectNames.Resolve(solution, "Kernel"));

		refusal.Message.ShouldBe(
			"No project in this solution is called 'Kernel'. It has Core, Tests. Name one of those, or give "
				+ "the path to its project file.");
	}

	[Test]
	public void Means_every_project_when_none_is_named()
	{
		using var workspace = Workspace(out var solution, "Core", "Tests");

		ProjectNames.ResolveOrAll(solution, null).Count.ShouldBe(2);
		ProjectNames.ResolveOrAll(solution, " ").Count.ShouldBe(2);
	}

	/// <summary>
	/// A tool comparing a project's name with a string for itself is a tool with a policy of its own,
	/// and policies drift: one widening to the whole solution, one answering with nothing, only some
	/// taking a path. So nothing in the worker but <see cref="ProjectNames"/> reads a project's name
	/// straight into a string comparison.
	/// </summary>
	[Test]
	public void No_tool_matches_a_project_name_except_through_the_one_helper()
	{
		var methods = MethodCalls.In(typeof(ProjectNames).Assembly)
			.GroupBy(call => call.Caller)
			.ToArray();

		// The helper itself is seen doing it, so an empty list below means the others do not rather
		// than that the check is blind.
		methods.Where(method => method.First().Owner == typeof(ProjectNames).FullName)
			.ShouldContain(method => ComparesAProjectName(method));

		var offenders = methods
			.Where(method => method.First().Owner != typeof(ProjectNames).FullName && ComparesAProjectName(method))
			.Select(method => method.Key)
			.Order(StringComparer.Ordinal)
			.ToArray();

		offenders.ShouldBeEmpty("a project is found by name through ProjectNames and nothing else");
	}

	private const string ProjectName = "Microsoft.CodeAnalysis.Project.get_Name";

	/// <summary>
	/// A project's name read and compared within a few instructions, which is what
	/// <c>string.Equals(project.Name, named, ...)</c> compiles to whether it is written in a lambda or in
	/// a loop. Reading a name for a progress line or a result and comparing other strings elsewhere in
	/// the same method is not matching a project, so the two have to be adjacent.
	/// </summary>
	private static bool ComparesAProjectName(IEnumerable<MethodCalls.Call> method)
	{
		var calls = method.OrderBy(call => call.At).ToArray();

		return calls.Any(read => read.Callee == ProjectName
			&& calls.Any(compare => compare.At > read.At && compare.At - read.At <= 4 && ComparesStrings(compare.Callee)));
	}

	/// <summary>
	/// The ways a project name gets compared with what a caller wrote: an equality with or without a
	/// comparison, or a case-insensitive comparer handed to a lookup.
	/// </summary>
	private static bool ComparesStrings(string callee) =>
		callee is "System.String.Equals" or "System.String.op_Equality"
			|| (callee.StartsWith("System.StringComparer.", StringComparison.Ordinal)
				&& callee.Contains("IgnoreCase", StringComparison.Ordinal));

	private static AdhocWorkspace Workspace(out Solution solution, params string[] names)
	{
		var workspace = new AdhocWorkspace();
		solution = workspace.CurrentSolution;

		foreach (var name in names)
		{
			var file = Path.Combine(Path.GetTempPath(), "ProjectNamesTests", name, $"{name}.csproj");

			solution = solution.AddProject(ProjectInfo.Create(
				ProjectId.CreateNewId(), VersionStamp.Default, name, name, LanguageNames.CSharp, filePath: file));
		}

		return workspace;
	}
}
