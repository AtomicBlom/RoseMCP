using System.Reflection;
using System.Runtime.CompilerServices;

using ModelContextProtocol.Server;

using RoseMcp.TestSupport;
using RoseMcp.Worker.Tools;

namespace RoseMcp.UnitTests;

/// <summary>
/// The one way a tool reaches the workspace, and the rule that it is the only one. A call that
/// does not follow the shared work still answers correctly, so nothing but a test notices a client
/// watching a silent call through a cold load.
/// </summary>
public sealed class WorkspaceCallsTests
{
	[Test]
	public async Task A_call_hears_the_shared_work_from_before_it_started_until_it_ends()
	{
		var shared = new SharedWorkProgress();
		using var loading = shared.Begin("Loading Thing.sln");
		var waiting = new CapturingProgress();

		var answer = await WorkspaceCalls.FollowingAsync(shared, waiting, async () =>
		{
			await Task.Yield();
			shared.Report("Loaded Core (1/2)", 50);

			return 42;
		});

		shared.Report("Loaded Tests (2/2)", 100);

		answer.ShouldBe(42);
		waiting.Reports.Select(report => report.Message).ShouldBe(["Loading Thing.sln", "Loaded Core (1/2)"]);
	}

	[Test]
	public async Task A_call_that_fails_stops_hearing_the_shared_work()
	{
		var shared = new SharedWorkProgress();
		var waiting = new CapturingProgress();

		await Should.ThrowAsync<InvalidOperationException>(() => WorkspaceCalls.FollowingAsync<int>(
			shared, waiting, () => throw new InvalidOperationException("The load failed.")));

		shared.Report("Reloading the solution", 10);

		waiting.Reports.ShouldBeEmpty();
	}

	/// <summary>
	/// A tool handed the host or the shared progress can reach the workspace without following the
	/// shared work, and the first tool to copy the wrong neighbour would. So none is given them,
	/// whether through its constructor or through a parameter the server fills from its services.
	/// </summary>
	[Test]
	public void No_tool_can_reach_the_workspace_except_through_the_calls()
	{
		Type[] bypasses = [typeof(WorkspaceHost), typeof(SharedWorkProgress), typeof(WorkspaceSession)];

		var toolTypes = typeof(WorkspaceCalls).Assembly.GetTypes()
			.Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
			.ToList();

		toolTypes.ShouldNotBeEmpty();

		const BindingFlags Everything =
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

		var offenders =
			from type in toolTypes
			from method in type.GetConstructors(Everything).Cast<MethodBase>().Concat(type.GetMethods(Everything))
			where !IsCompilerWritten(method)
			from parameter in method.GetParameters()
			where bypasses.Contains(parameter.ParameterType)
			select $"{type.Name}.{method.Name}({parameter.ParameterType.Name} {parameter.Name})";

		offenders.ShouldBeEmpty("a tool reaches the workspace through WorkspaceCalls and nothing else");
	}

	/// <summary>
	/// A method the compiler wrote rather than the tool's author: a lambda handed to the calls that
	/// captures nothing but fields is lowered to an instance method on the tool type, taking the
	/// session it was given, which is the helper doing its job rather than a way round it. Lambda
	/// methods are not reliably marked as generated, so their unspeakable names are checked too.
	/// </summary>
	private static bool IsCompilerWritten(MethodBase method) =>
		method.IsDefined(typeof(CompilerGeneratedAttribute)) || method.Name.StartsWith('<');
}
