using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The exact set of tools a client is offered, asserted rather than assumed.
/// <para>
/// The surface is what a client budgets context for, so adding to it is a decision and not an
/// implementation detail -- and it is made in three <c>WithTools</c> lines that nothing reads back.
/// Those same three lines are the only thing keeping the host-internal names off the wire: the
/// worker registers <c>rose_worker_info</c> and the live-app host registers twenty
/// <c>rose_live_app_*</c> tools, both through <c>WithToolsFromAssembly</c> in their own processes,
/// and the broker reaches them as an MCP client rather than by declaring them.
/// </para>
/// <para>
/// Failing here means the surface changed. Update the list in the same commit, which is the record
/// of what was added and when.
/// </para>
/// </summary>
public sealed class ToolSurfaceTests
{
	/// <summary>
	/// Everything the Roslyn half declares, on every operating system.
	/// </summary>
	private static readonly string[] Roslyn =
	[
		ToolNames.AddFile,
		ToolNames.AddMember,
		ToolNames.AddUsing,
		ToolNames.ApplyCodeFix,
		ToolNames.BuildFreshness,
		ToolNames.ChangeSignature,
		ToolNames.Diagnostics,
		ToolNames.FindImplementations,
		ToolNames.FindReferences,
		ToolNames.FormatDocuments,
		ToolNames.ListCodeFixes,
		ToolNames.ListGeneratedDocuments,
		ToolNames.MoveTypeToFile,
		ToolNames.ReadGeneratedDocument,
		ToolNames.RenameSymbol,
		ToolNames.ReplaceBody,
		ToolNames.ReplaceDocComment,
		ToolNames.ReplaceMember,
		ToolNames.ResolveName,
		ToolNames.SearchSymbols,
		ToolNames.SetAttribute,
		ToolNames.SymbolInfo,
		ToolNames.WorkspaceClose,
		ToolNames.WorkspaceOpen,
		ToolNames.WorkspaceReload,
		ToolNames.WorkspaceStatus,
	];

	/// <summary>
	/// The live-app half, which is registered only on Windows because the debugger is ICorDebug and
	/// dbgshim. A declared tool that cannot run is worse than an absent one, so this list being
	/// conditional is the behaviour under test rather than a convenience.
	/// </summary>
	private static readonly string[] LiveApp =
	[
		ToolNames.DebugAddTracepoint,
		ToolNames.DebugAttach,
		ToolNames.DebugContinue,
		ToolNames.DebugDetach,
		ToolNames.DebugEvaluate,
		ToolNames.DebugEvents,
		ToolNames.DebugLaunch,
		ToolNames.DebugLaunchUwp,
		ToolNames.DebugList,
		ToolNames.DebugListBreakpoints,
		ToolNames.DebugListTracepoints,
		ToolNames.DebugRemoveBreakpoint,
		ToolNames.DebugRemoveTracepoint,
		ToolNames.DebugSetBreakpoint,
		ToolNames.DebugStep,
		ToolNames.XamlApply,
		ToolNames.XamlDeselect,
		ToolNames.XamlProperties,
		ToolNames.XamlSelectElement,
		ToolNames.XamlSelectMode,
		ToolNames.XamlSelection,
		ToolNames.XamlTree,
	];

	/// <summary>
	/// Names that exist in <see cref="ToolNames"/> and must never reach a client. They are the tools
	/// the broker <em>calls</em>, in the worker and in the live-app host, and a client offered them
	/// would be offered a tool with no session to run it against.
	/// </summary>
	private static readonly string[] HostInternal =
	[
		ToolNames.WorkerInfo,
		ToolNames.LiveAppInfo,
		ToolNames.LiveAppEvents,
		ToolNames.LiveAppDetach,
		ToolNames.LiveAppAddTracepoint,
		ToolNames.LiveAppListTracepoints,
		ToolNames.LiveAppRemoveTracepoint,
		ToolNames.LiveAppSetBreakpoint,
		ToolNames.LiveAppListBreakpoints,
		ToolNames.LiveAppRemoveBreakpoint,
		ToolNames.LiveAppContinue,
		ToolNames.LiveAppStep,
		ToolNames.LiveAppEvaluate,
		ToolNames.LiveAppXamlTree,
		ToolNames.LiveAppXamlProperties,
		ToolNames.LiveAppXamlApply,
		ToolNames.LiveAppXamlSelectMode,
		ToolNames.LiveAppXamlSelection,
		ToolNames.LiveAppXamlDeselect,
		ToolNames.LiveAppXamlSelectElement,
	];

	[Fact]
	public void The_broker_offers_exactly_the_listed_tools()
	{
		var expected = OperatingSystem.IsWindows() ? [.. Roslyn, .. LiveApp] : Roslyn;

		Assert.Equal(Sorted(expected), Advertised());
	}

	[Fact]
	public void Every_advertised_name_starts_with_the_server_prefix()
	{
		foreach (var name in Advertised())
		{
			Assert.StartsWith("rose_", name, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// The names the broker calls in another process, never declares. Checked against the whole
	/// surface rather than against the registration, because the failure being guarded is a tool
	/// class gaining a method that happens to reuse one of these constants.
	/// </summary>
	[Fact]
	public void The_tools_the_broker_calls_elsewhere_are_not_offered()
	{
		var advertised = Advertised().ToHashSet(StringComparer.Ordinal);

		foreach (var name in HostInternal)
		{
			Assert.DoesNotContain(name, advertised);
		}
	}

	/// <summary>
	/// The live-app half is Windows-only, and the two lists must not overlap: a tool that appears in
	/// both would be gated by the operating system in one place and not the other.
	/// </summary>
	[Fact]
	public void The_two_halves_of_the_surface_are_disjoint()
	{
		Assert.Empty(Roslyn.Intersect(LiveApp, StringComparer.Ordinal));
	}

	/// <summary>
	/// Read from the registration rather than by reflecting over the assembly, because the assembly
	/// holds <c>LiveAppDebugTools</c> on every platform and only the registration knows the gate.
	/// </summary>
	private static string[] Advertised()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		return Sorted([.. provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name)]);
	}

	private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];
}
