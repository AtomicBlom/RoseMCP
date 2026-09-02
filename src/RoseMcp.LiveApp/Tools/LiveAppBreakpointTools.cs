using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>Stopping-breakpoint management and continue for this host's target, forwarded by the broker.</summary>
[McpServerToolType]
public sealed class LiveAppBreakpointTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppSetBreakpoint,
		Title = "Set a stopping breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Set a stopping breakpoint at a method by name; it holds the target on hit until continued.")]
	public LiveBreakpoint Set(
		[Description("[Assembly!]Namespace.Type.Method, e.g. MyApp.Widget.Refresh.")] string location,
		[Description("Seconds to hold before auto-continuing; default 30.")] int? autoContinueSeconds = null)
		=> host.SetBreakpoint(location, autoContinueSeconds);

	[McpServerTool(
		Name = ToolNames.LiveAppListBreakpoints,
		Title = "List stopping breakpoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("This session's stopping breakpoints and whether each is bound.")]
	public LiveBreakpointList List() => host.ListBreakpoints();

	[McpServerTool(
		Name = ToolNames.LiveAppRemoveBreakpoint,
		Title = "Remove a stopping breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Remove a stopping breakpoint by id, returning the remaining set.")]
	public LiveBreakpointList Remove(
		[Description("The breakpoint id from set.")] string id)
		=> host.RemoveBreakpoint(id);

	[McpServerTool(
		Name = ToolNames.LiveAppContinue,
		Title = "Continue from a breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Resume a target held at a stopping breakpoint.")]
	public LiveContinueResult Continue() => host.Continue();

	[McpServerTool(
		Name = ToolNames.LiveAppStep,
		Title = "Step",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Step a target held at a breakpoint: \"in\", \"over\", or \"out\".")]
	public LiveContinueResult Step(
		[Description("in, over, or out.")] string mode)
		=> host.Step(mode);
}
