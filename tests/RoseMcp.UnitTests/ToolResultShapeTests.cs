using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The two facts every answer carries: which workspace produced it, and which snapshot of that
/// workspace it describes.
/// <para>
/// CLAUDE.md states both at the MCP boundary and says attribution is added once "so a tool added
/// later cannot forget it". Nothing proved it. Attribution was applied by testing the result
/// against a base type at run time, so a tool answering with something else was attributed with
/// nothing and said so nowhere -- and <c>rose_workspace_close</c> answered with a sentence naming
/// no workspace at all, which is the shape this would have caught on the day it was written.
/// </para>
/// <para>
/// Enumerated from the assembly rather than from the registration, because the registration gates
/// the live-app half by operating system and this rule holds on every platform. The cost of
/// getting it from reflection is that the exemptions below are a list; the cost of not having it is
/// that the rule is a habit.
/// </para>
/// </summary>
public sealed class ToolResultShapeTests
{
	/// <summary>
	/// The prefixes of the live-app surface, whose answers are scoped to a debugged process rather
	/// than to a loaded solution. A process has no revision and belongs to no workspace: what
	/// identifies one of those answers is its session, which its own result carries.
	/// <para>
	/// A prefix rather than a list of names, so adding a debug tool does not need this file edited
	/// -- and <see cref="The_live_app_surface_is_exactly_what_those_prefixes_name"/> is what stops
	/// the prefix quietly widening to cover a Roslyn tool.
	/// </para>
	/// </summary>
	private static readonly string[] ProcessScoped = ["rose_debug_", "rose_xaml_"];

	/// <summary>
	/// Results with no revision, and why each is honest without one. Both describe a workspace from
	/// outside any snapshot of it: one before there is a snapshot to report and one after the last
	/// one is gone. A zero in either would read as a snapshot rather than as an absence.
	/// </summary>
	private static readonly Dictionary<string, string> WithoutRevision = new(StringComparer.Ordinal)
	{
		[nameof(WorkspaceSummary)] =
			"rose_workspace_open answers while the load is still running, and a revision, a project "
				+ "list and a diagnostic list all have no honest value yet.",
		[nameof(WorkspaceClosed)] =
			"rose_workspace_close answers about a workspace that is no longer loaded, so there is no "
				+ "snapshot for a revision to identify.",
	};

	/// <summary>
	/// Every tool that answers about a solution answers with something the broker can attribute.
	/// The run-time half of attribution is a type test, and a result that fails it is passed through
	/// silently, so this is the only thing standing between "attributed" and "attributed in
	/// practice so far".
	/// </summary>
	[Test]
	public void Every_workspace_tool_answers_with_an_attributable_result()
	{
		foreach (var (name, result) in WorkspaceScopedTools())
		{
			Assert.True(
				typeof(WorkspaceScopedResult).IsAssignableFrom(result),
				$"{name} answers with {result.Name}, which does not derive from WorkspaceScopedResult, "
					+ "so the broker cannot say which workspace answered.");
		}
	}

	/// <summary>
	/// And that each of those results says which snapshot it describes. A result with no revision
	/// cannot be checked for staleness by a caller holding two of them, which is the whole reason
	/// the freshness barrier reports a number rather than a promise.
	/// </summary>
	[Test]
	public void Every_workspace_tool_result_carries_a_revision()
	{
		foreach (var (name, result) in WorkspaceScopedTools())
		{
			if (WithoutRevision.ContainsKey(result.Name)) continue;

			Assert.True(
				result.GetProperty("Revision", BindingFlags.Public | BindingFlags.Instance) is not null,
				$"{name} answers with {result.Name}, which carries no revision. Add one, or add the "
					+ "type to WithoutRevision with the reason it is honest without one.");
		}
	}

	/// <summary>
	/// The exemptions name types that exist and are actually reachable from a tool, because a list
	/// carrying a name nothing returns any more is a list nobody believes.
	/// </summary>
	[Test]
	public void Nothing_is_exempt_from_a_revision_for_nothing()
	{
		var returned = WorkspaceScopedTools().Select(tool => tool.Result.Name).ToHashSet(StringComparer.Ordinal);

		foreach (var (name, reason) in WithoutRevision)
		{
			Assert.Contains(name, returned);
			Assert.NotEmpty(reason);
		}
	}

	/// <summary>
	/// The compile-time half. <c>WorkspaceManager.CallAsync</c> constrains its result to something
	/// attributable, so a tool answering with anything else fails to build rather than answering
	/// without attribution -- which is what "a tool added later cannot forget it" has to mean to be
	/// worth stating. Asserted because a constraint is one word and deleting it breaks nothing that
	/// runs.
	/// </summary>
	[Test]
	public void The_forwarding_path_only_accepts_an_attributable_result()
	{
		var forward = typeof(WorkspaceManager)
			.GetMethod(nameof(WorkspaceManager.CallAsync), BindingFlags.Public | BindingFlags.Instance);

		Assert.NotNull(forward);

		var constraints = forward!.GetGenericArguments().Single().GetGenericParameterConstraints();

		Assert.Contains(typeof(WorkspaceScopedResult), constraints);
	}

	/// <summary>
	/// The prefixes that buy an exemption name the live-app surface and nothing else. Without this
	/// a Roslyn tool named <c>rose_xaml_something</c> would inherit an exemption written for a
	/// debugger, which is how a rule with a prefix in it stops meaning what it says.
	/// </summary>
	[Test]
	public void The_live_app_surface_is_exactly_what_those_prefixes_name()
	{
		foreach (var (name, declaring) in DeclaredTools().Select(tool => (tool.Name, tool.Method.DeclaringType!)))
		{
			var byPrefix = ProcessScoped.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
			var byType = declaring.Name == "LiveAppDebugTools";

			Assert.Equal(byType, byPrefix);
		}
	}

	/// <summary>The tools that answer about a solution, with the type each answers with.</summary>
	private static IEnumerable<(string Name, Type Result)> WorkspaceScopedTools() =>
		DeclaredTools()
			.Where(tool => !ProcessScoped.Any(prefix => tool.Name.StartsWith(prefix, StringComparison.Ordinal)))
			.Select(tool => (tool.Name, Result: Returned(tool.Method)));

	/// <summary>
	/// Every tool the broker declares, from the assembly. <c>[McpServerToolType]</c> is what the SDK
	/// scans, so scanning the same thing means a tool class added later is covered without this file
	/// naming it.
	/// </summary>
	private static IEnumerable<(string Name, MethodInfo Method)> DeclaredTools()
	{
		var types = typeof(WorkspaceManager).Assembly
			.GetTypes()
			.Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

		foreach (var type in types)
		{
			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				var tool = method.GetCustomAttribute<McpServerToolAttribute>();
				if (tool?.Name is not null) yield return (tool.Name, method);
			}
		}
	}

	/// <summary>What a tool answers with, with the Task unwrapped.</summary>
	private static Type Returned(MethodInfo method)
	{
		var returned = method.ReturnType;

		return returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(Task<>)
			? returned.GetGenericArguments()[0]
			: returned;
	}
}
