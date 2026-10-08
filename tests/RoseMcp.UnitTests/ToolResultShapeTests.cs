using System.Reflection;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What every answer is, and the two facts a workspace's answer carries: which workspace produced it,
/// and which snapshot of that workspace it describes.
/// <para>
/// CLAUDE.md states both facts at the MCP boundary and says attribution is added once "so a tool added
/// later cannot forget it". Nothing proved it. Attribution was applied by testing the result
/// against a base type at run time, so a tool answering with something else was attributed with
/// nothing and said so nowhere -- and <c>rose_workspace_close</c> answered with a sentence naming
/// no workspace at all, which is the shape this would have caught on the day it was written.
/// </para>
/// <para>
/// Beneath both facts is the shape itself: every tool answers with a record. That holds on the
/// live-app surface too, which is excused from workspace attribution because a debugged process
/// belongs to no workspace, and from nothing else -- a sentence there names no session, exactly as one
/// here names no workspace.
/// </para>
/// <para>
/// Enumerated from the assembly rather than from the registration, because the registration gates the
/// live-app half by operating system and this rule holds on every platform. The cost of
/// getting it from reflection is that the exemptions below are a list; the cost of not having it is
/// that the rule is a habit.
/// </para>
/// </summary>
public sealed class ToolResultShapeTests
{
	/// <summary>
	/// The prefixes of the live-app surface, whose answers are scoped to a debugged process rather
	/// than to a loaded solution. A process has no revision and belongs to no workspace, so these
	/// tools are excused from <em>workspace</em> attribution and nothing else: each still answers with
	/// a record, which <see cref="Every_tool_answers_with_a_record"/> holds without consulting this
	/// list, and none answers with a workspace's result, which
	/// <see cref="No_live_app_tool_answers_as_though_a_workspace_did"/> holds.
	/// <para>
	/// A prefix rather than a list of names, so adding a debug tool does not need this file edited
	/// -- and <see cref="The_live_app_surface_is_exactly_what_those_prefixes_name"/> is what stops
	/// the prefix quietly widening to cover a Roslyn tool.
	/// </para>
	/// </summary>
	private static readonly string[] ExemptFromWorkspaceAttribution = ["rose_debug_", "rose_xaml_"];

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
	/// Every tool, on either surface, answers with a record rather than with a sentence or a bare
	/// value. A sentence names nothing a caller can check -- which workspace, which session -- and gives
	/// an agent nothing to branch on but its wording; a bare value cannot gain a field without changing
	/// the tool's shape. Asserted over the whole surface without consulting
	/// <see cref="ExemptFromWorkspaceAttribution"/>, because that excuses the live-app half from saying
	/// which workspace answered, not from answering with a result.
	/// </summary>
	[Test]
	public void Every_tool_answers_with_a_record()
	{
		foreach (var (name, method) in DeclaredSurface.Tools())
		{
			var result = DeclaredSurface.Returned(method);

			IsRecord(result).ShouldBeTrue(
				$"{name} answers with {result.Name}, which is not a record. Give it a result record that "
					+ "carries what answered and the outcome as fields.");
		}
	}

	/// <summary>
	/// That <see cref="IsRecord"/> tells the shapes it exists to refuse from the shape it requires,
	/// since a test of a test's helper is the only thing that notices it accepting everything.
	/// </summary>
	[Test]
	public void A_sentence_a_bare_task_and_a_plain_class_are_not_records()
	{
		IsRecord(typeof(string)).ShouldBeFalse();
		IsRecord(typeof(Task)).ShouldBeFalse();
		IsRecord(typeof(bool)).ShouldBeFalse();
		IsRecord(typeof(object)).ShouldBeFalse();
		IsRecord(typeof(LiveSessionDetached)).ShouldBeTrue();
		IsRecord(typeof(WorkspaceClosed)).ShouldBeTrue();
	}

	/// <summary>
	/// The live-app half answers about a process, so none of its results claims to be a workspace's.
	/// One that did would pass through the broker unattributed -- attribution happens on the workspace
	/// path, which a live-app call never takes -- and reach the caller with a workspace field naming
	/// nothing.
	/// </summary>
	[Test]
	public void No_live_app_tool_answers_as_though_a_workspace_did()
	{
		var liveApp = DeclaredSurface.Tools().Where(tool => IsLiveApp(tool.Name)).ToList();

		liveApp.ShouldNotBeEmpty();

		foreach (var (name, method) in liveApp)
		{
			var result = DeclaredSurface.Returned(method);

			typeof(WorkspaceScopedResult).IsAssignableFrom(result).ShouldBeFalse(
				$"{name} answers about a debugged process with {result.Name}, which derives from "
					+ "WorkspaceScopedResult and so claims a workspace answered.");
		}
	}

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
			typeof(WorkspaceScopedResult).IsAssignableFrom(result).ShouldBeTrue(
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

			(result.GetProperty("Revision", BindingFlags.Public | BindingFlags.Instance) is not null).ShouldBeTrue(
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
			returned.ShouldContain(name);
			reason.ShouldNotBeEmpty();
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

		forward.ShouldNotBeNull();

		var constraints = forward!.GetGenericArguments().Single().GetGenericParameterConstraints();

		constraints.ShouldContain(typeof(WorkspaceScopedResult));
	}

	/// <summary>
	/// The prefixes that buy an exemption name the live-app surface and nothing else. Without this
	/// a Roslyn tool named <c>rose_xaml_something</c> would inherit an exemption written for a
	/// debugger, which is how a rule with a prefix in it stops meaning what it says.
	/// </summary>
	[Test]
	public void The_live_app_surface_is_exactly_what_those_prefixes_name()
	{
		foreach (var (name, declaring) in DeclaredSurface.Tools().Select(tool => (tool.Name, tool.Method.DeclaringType!)))
		{
			var byPrefix = IsLiveApp(name);
			var byType = declaring.Name == "LiveAppDebugTools";

			byPrefix.ShouldBe(byType);
		}
	}

	/// <summary>The tools that answer about a solution, with the type each answers with.</summary>
	private static IEnumerable<(string Name, Type Result)> WorkspaceScopedTools() =>
		DeclaredSurface.Tools()
			.Where(tool => !IsLiveApp(tool.Name))
			.Select(tool => (tool.Name, Result: DeclaredSurface.Returned(tool.Method)));

	/// <summary>
	/// Whether a tool belongs to the live-app surface, by <see cref="ExemptFromWorkspaceAttribution"/>.
	/// </summary>
	private static bool IsLiveApp(string name) =>
		ExemptFromWorkspaceAttribution.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

	/// <summary>
	/// Whether a type is a record. The compiler gives every record class a public <c>&lt;Clone&gt;$</c>
	/// method for <c>with</c> to call, and no source can declare a member with that name.
	/// </summary>
	private static bool IsRecord(Type type) =>
		type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
}
