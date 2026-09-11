using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>
/// Reading a stop: the call stack, a frame's variables, what is inside a value, the target's
/// threads, and the operator hold that keeps a stop still while somebody reads it.
/// <para>
/// These are forwarded by the operator API rather than by an agent-facing <c>rose_debug_*</c> tool.
/// The two surfaces answer different questions: an agent asks a question and reads one answer, so
/// the stop event's captured frame and <c>rose_debug_evaluate</c> serve it, while a person scrolls a
/// stack, opens a tree and needs the target to stay still for a minute. Giving an agent the second
/// shape would mean giving it the hold, and a hold an agent forgets to release is somebody's
/// application frozen for ten minutes.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LiveAppInspectionTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppFrames,
		Title = "Read a stopped thread's call stack",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("A page of a stopped thread's managed call stack, with file and line where symbols allow.")]
	public LiveStackFrames Frames(
		[Description(ToolDescriptions.FrameThreadIdArgument)] int? threadId = null,
		[Description(ToolDescriptions.FrameOffsetArgument)] int offset = 0,
		[Description(ToolDescriptions.FrameLimitArgument)] int? limit = null)
		=> host.ReadFrames(threadId, offset, limit);

	[McpServerTool(
		Name = ToolNames.LiveAppFrameVariables,
		Title = "Read a frame's variables",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("One frame's arguments and locals, each with the path that expands it.")]
	public LiveFrameVariables FrameVariables(
		[Description(ToolDescriptions.FrameIndexArgument)] int frameIndex,
		[Description(ToolDescriptions.FrameThreadIdArgument)] int? threadId = null)
		=> host.ReadFrameVariables(frameIndex, threadId);

	[McpServerTool(
		Name = ToolNames.LiveAppExpand,
		Title = "Expand a value",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("An object's fields or an array's elements, read from memory; runs no debuggee code.")]
	public LiveValueExpansion Expand(
		[Description(ToolDescriptions.ValuePathArgument)] string path,
		[Description(ToolDescriptions.FrameIndexArgument)] int frameIndex = 0,
		[Description(ToolDescriptions.FrameThreadIdArgument)] int? threadId = null)
		=> host.ExpandValue(path, frameIndex, threadId);

	[McpServerTool(
		Name = ToolNames.LiveAppThreads,
		Title = "List the stopped target's threads",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Every managed thread of a stopped target, the held one first, with its top frame.")]
	public LiveThreadList Threads() => host.ReadThreads();

	[McpServerTool(
		Name = ToolNames.LiveAppHold,
		Title = "Hold a stop",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Suspend a stop's auto-continue timer so a reader can work, or give it back.")]
	public LiveHoldResult Hold(
		[Description(ToolDescriptions.HoldSecondsArgument)] int? seconds = null,
		[Description(ToolDescriptions.HoldReleaseArgument)] bool release = false)
		=> host.Hold(seconds, release);

	[McpServerTool(
		Name = ToolNames.LiveAppSearchMethods,
		Title = "Find a method to break in",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Methods of the target's loaded modules matching a partial name, best first.")]
	public LiveMethodMatches SearchMethods(
		[Description(ToolDescriptions.MethodQueryArgument)] string query,
		[Description(ToolDescriptions.MethodSearchLimitArgument)] int limit = 30)
		=> host.SearchMethods(query, limit);

	[McpServerTool(
		Name = ToolNames.LiveAppMethodSource,
		Title = "Read a method's source and breakable positions",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("A method's source text with every position inside it a breakpoint can be set at.")]
	public LiveMethodSource MethodSource(
		[Description(ToolDescriptions.MethodSourceLocationArgument)] string location)
		=> host.ReadMethodSource(location);
}
