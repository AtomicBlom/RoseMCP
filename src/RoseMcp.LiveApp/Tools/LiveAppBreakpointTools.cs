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
		Title = "Set stopping breakpoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Set stopping breakpoints at methods by name; each holds the target on hit until continued, and each request's outcome is its own entry.")]
	public LiveBreakpointBatch Set(
		[Description(ToolDescriptions.BreakpointsArgument)] SetBreakpointRequest[] breakpoints)
		=> host.SetBreakpoints(breakpoints);

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
		Title = "Remove stopping breakpoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Remove stopping breakpoints by id, each id's outcome its own entry, returning the remaining set.")]
	public LiveBreakpointRemoval Remove(
		[Description(ToolDescriptions.BreakpointIdsArgument)] string[] breakpointIds)
		=> host.RemoveBreakpoints(breakpointIds);

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
		[Description(ToolDescriptions.StepModeArgument)] string mode)
		=> host.Step(mode);

	[McpServerTool(
		Name = ToolNames.LiveAppEvaluate,
		Title = "Evaluate at a stop",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Evaluate a field-access expression against the stopped frame; runs no debuggee code.")]
	public LiveEvaluation Evaluate(
		[Description(ToolDescriptions.EvaluateExpressionArgument)] string expression,
		[Description(ToolDescriptions.EvaluateMaxLengthArgument)] int? maxLength = null)
		=> host.Evaluate(expression, maxLength);
}
