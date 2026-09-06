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
		[Description(ToolDescriptions.BreakpointLocationArgument)] string location,
		[Description(ToolDescriptions.AutoContinueSecondsArgument)] int? autoContinueSeconds = null,
		[Description(ToolDescriptions.BreakpointConditionArgument)] string? condition = null)
		=> host.SetBreakpoint(location, autoContinueSeconds, condition);

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
		[Description(ToolDescriptions.BreakpointIdArgument)] string breakpointId)
		=> host.RemoveBreakpoint(breakpointId);

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
		[Description(ToolDescriptions.EvaluateExpressionArgument)] string expression)
		=> host.Evaluate(expression);
}
