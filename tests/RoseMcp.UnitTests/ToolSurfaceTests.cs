using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
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
		ToolNames.DeleteMember,
		ToolNames.Diagnostics,
		ToolNames.FindImplementations,
		ToolNames.FindReferences,
		ToolNames.FormatDocuments,
		ToolNames.ListCodeFixes,
		ToolNames.ListGeneratedDocuments,
		ToolNames.MoveMember,
		ToolNames.MoveTypeToFile,
		ToolNames.Outline,
		ToolNames.ProjectGraph,
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
		ToolNames.LiveAppFrames,
		ToolNames.LiveAppFrameVariables,
		ToolNames.LiveAppExpand,
		ToolNames.LiveAppThreads,
		ToolNames.LiveAppHold,
		ToolNames.LiveAppXamlTree,
		ToolNames.LiveAppXamlProperties,
		ToolNames.LiveAppXamlApply,
		ToolNames.LiveAppXamlSelectMode,
		ToolNames.LiveAppXamlSelection,
		ToolNames.LiveAppXamlDeselect,
		ToolNames.LiveAppXamlSelectElement,
	];

	/// <summary>
	/// The tools that may declare themselves read-only. The SDK defines that as having "no side
	/// effects beyond computational resource usage", and clients use it to skip asking the user, so
	/// the hint is a promise made on the user's behalf rather than a description of the return type.
	/// <para>
	/// A list rather than an assertion per tool, so a new tool cannot arrive annotated read-only
	/// without somebody adding it here and saying why. rose_workspace_open starts a worker process
	/// that loads a gigabyte of solution and rose_debug_attach attaches a debugger to another process;
	/// both were on this list and neither belongs on it.
	/// </para>
	/// <para>
	/// rose_xaml_properties is here and is not quite honest about it: reading an element's properties
	/// materialises the collection ones, so a second read reports them as set. Nothing the app draws
	/// changes, and the tool says so itself (#97).
	/// </para>
	/// </summary>
	private static readonly string[] ReadOnly =
	[
		ToolNames.BuildFreshness,
		ToolNames.DebugEvaluate,
		ToolNames.DebugEvents,
		ToolNames.DebugList,
		ToolNames.DebugListBreakpoints,
		ToolNames.DebugListTracepoints,
		ToolNames.Diagnostics,
		ToolNames.FindImplementations,
		ToolNames.FindReferences,
		ToolNames.ListCodeFixes,
		ToolNames.ListGeneratedDocuments,
		ToolNames.Outline,
		ToolNames.ProjectGraph,
		ToolNames.ReadGeneratedDocument,
		ToolNames.ResolveName,
		ToolNames.SearchSymbols,
		ToolNames.SymbolInfo,
		ToolNames.WorkspaceStatus,
		ToolNames.XamlProperties,
		ToolNames.XamlSelection,
		ToolNames.XamlTree,
	];

	[Test]
	public void The_broker_offers_exactly_the_listed_tools()
	{
		var expected = OperatingSystem.IsWindows() ? [.. Roslyn, .. LiveApp] : Roslyn;

		Assert.Equal(Sorted(expected), Advertised());
	}

	[Test]
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
	[Test]
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
	[Test]
	public void The_two_halves_of_the_surface_are_disjoint()
	{
		Assert.Empty(Roslyn.Intersect(LiveApp, StringComparer.Ordinal));
	}

	/// <summary>
	/// Which tools promise to have no side effects, asserted against the list rather than trusted. A
	/// client skips confirmation on the strength of the hint, so one on a tool that starts a process
	/// or attaches a debugger spends the user's consent without asking for it.
	/// </summary>
	[Test]
	public void Only_the_listed_tools_call_themselves_read_only()
	{
		var advertised = Advertised().ToHashSet(StringComparer.Ordinal);
		var claimed = Sorted(ReadOnlyAdvertised());

		Assert.Equal(Sorted(ReadOnly.Where(advertised.Contains)), claimed);
	}

	/// <summary>
	/// What the listing carries after the trim. The output schemas were a third of the wire and hold
	/// no prose at all, so a model that read one would learn field names and nothing about what they
	/// mean; the carriage returns come from raw string literals in CRLF files and at least one client
	/// passes them to the model verbatim.
	/// </summary>
	[Test]
	public void The_listing_carries_no_output_schema_and_no_carriage_return()
	{
		foreach (var tool in Listed())
		{
			Assert.Null(tool.OutputSchema);
			Assert.DoesNotContain('\r', tool.Description ?? string.Empty);
			Assert.DoesNotContain("\\r", tool.InputSchema.GetRawText(), StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// And that the trim is wired into the listing rather than only available to be called. The two
	/// assertions are separate because a filter registered without doing the work and work available
	/// but never applied are different failures and neither implies the other.
	/// </summary>
	[Test]
	public void The_listing_passes_through_a_filter()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

		Assert.NotEmpty(options.Filters.Request.ListToolsFilters);
	}

	/// <summary>
	/// The tools the instructions do not route, named so adding one is a decision. Each is the other
	/// half of a pair whose first half is routed -- you list or remove what you have just set, you
	/// deselect what you have selected -- so a session that reached the naming tool has the counterpart
	/// in that tool's own description. rose_workspace_close is housekeeping nothing else depends on.
	/// <para>
	/// rose_xaml_select_element was here on the reasoning that a read hands out the handle it takes.
	/// That held while arming select mode was routed beside it; with arming gone from the surface,
	/// selecting by handle is the only way an agent picks an element at all, and nothing else names it.
	/// </para>
	/// </summary>
	private static readonly string[] Unrouted =
	[
		ToolNames.DebugList,
		ToolNames.DebugListBreakpoints,
		ToolNames.DebugListTracepoints,
		ToolNames.DebugRemoveBreakpoint,
		ToolNames.DebugRemoveTracepoint,
		ToolNames.WorkspaceClose,
		ToolNames.XamlDeselect,
	];

	/// <summary>
	/// Every advertised tool is either routed by the instructions or exempted here. The instructions
	/// are read at every initialize, before the model has chosen an approach, so a tool missing from
	/// them is a tool that loses to grep before its own description is ever reached -- and four were
	/// missing with nothing to notice.
	/// </summary>
	[Test]
	public void Every_advertised_tool_is_routed_or_exempt()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var instructions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions;

		Assert.NotNull(instructions);

		var exempt = Unrouted.ToHashSet(StringComparer.Ordinal);

		foreach (var name in Advertised().Where(name => !exempt.Contains(name)))
		{
			Assert.Contains(name, instructions, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// And that the exemption list is not carrying a name the instructions route after all, which is
	/// how a list like this stops meaning anything.
	/// </summary>
	[Test]
	public void Nothing_exempt_is_routed_anyway()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var instructions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions ?? string.Empty;
		var advertised = Advertised().ToHashSet(StringComparer.Ordinal);

		foreach (var name in Unrouted.Where(advertised.Contains))
		{
			Assert.DoesNotContain(name, instructions, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// The instructions are always in context, whether or not C# comes up, so their length is a budget
	/// rather than a preference. They were 11,340 characters restating the descriptions line for line.
	/// </summary>
	[Test]
	public void The_instructions_stay_within_their_budget()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var instructions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions ?? string.Empty;

		Assert.InRange(instructions.Length, 1, 4000);
	}

	/// <summary>
	/// The tools as a client is sent them, which is not what the registration holds: the SDK generates
	/// an output schema per tool and the trim takes it back off on the way out.
	/// </summary>
	private static Tool[] Listed()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var tools = provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool).ToArray();

		foreach (var tool in tools) ToolListing.Trim(tool);

		return tools;
	}

	/// <summary>The advertised tools whose annotations say they have no side effects.</summary>
	private static string[] ReadOnlyAdvertised()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		return
		[
			.. provider.GetServices<McpServerTool>()
				.Select(tool => tool.ProtocolTool)
				.Where(tool => tool.Annotations?.ReadOnlyHint == true)
				.Select(tool => tool.Name),
		];
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
