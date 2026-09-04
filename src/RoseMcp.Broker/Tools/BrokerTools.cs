using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// The client-facing tool surface. Each tool resolves a workspace, then forwards to that
/// workspace's worker, whose tools carry the same names minus the workspace argument.
/// </summary>
[McpServerToolType]
public sealed class BrokerTools(WorkspaceManager workspaces)
{
	private const string WorkspaceHelp =
		"Path to a solution, project, or any file inside one. Usually omitted: it is inferred from the "
			+ "other arguments, or from the working directory. Needed only where a directory holds "
			+ "several solutions and none of them is pinned, which is reported when it happens.";

	/// <summary>
	/// Starts a load and returns without waiting for it (#44).
	/// <para>
	/// This used to be <c>rose_workspace_status</c> under a second name -- the same two lines, the same
	/// result type, the same arguments -- which is why its own description had to admit it was "rarely
	/// needed on its own". Not waiting is what it is for, and what makes it a different tool from the
	/// one that reports.
	/// </para>
	/// <para>
	/// It answers with <see cref="WorkspaceSummary"/> rather than
	/// <see cref="WorkspaceStatusReport"/>, and the choice is forced rather than stylistic. A report
	/// requires a revision, a project list and a load-diagnostic list, and a solution that is still
	/// loading has no honest value for any of them -- an empty list is not "not known yet", it is the
	/// claim that there are none, which is what "status may not report a field it cannot fill" was
	/// written about. The state carries that instead: <see cref="WorkspaceState.Loading"/> is a fact
	/// about the workspace with a next action in it, where a null field would only be an absence. A
	/// summary is also the only one of the two shaped to carry progress, since it holds the activity
	/// log's running operations and their percentages, and watching a load is half the point.
	/// </para>
	/// <para>
	/// Every field it carries is broker-side, so nothing here goes through the read barrier and
	/// polling cannot block on the load it is describing. That is the whole trick.
	/// </para>
	/// </summary>
	[McpServerTool(
		Name = ToolNames.WorkspaceOpen,
		Title = "Open a solution",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.WorkspaceOpen)]
	public async Task<WorkspaceSummary> OpenAsync(
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default)
	{
		// Returns as soon as the process is up and the MCP handshake is done -- a second or so, not the
		// two minutes the load takes. The load is already in flight by then: a worker starts loading
		// when its process does, and the broker asks it for status the moment it connects precisely so
		// a load with no client waiting on it is still visible.
		var worker = await workspaces.GetOrStartAsync(WorkspaceHints.From(workspace), cancellationToken);
		var summary = worker.Describe();

		// Only while it is still loading, so a solution that was already open says nothing and the
		// caller has nothing to wait for -- which is the common case and should read as finished.
		return summary.State == WorkspaceState.Loading
			? summary with { Notices = [.. summary.Notices, StillLoadingNotice] }
			: summary;
	}

	/// <summary>
	/// What happens next, said in the result rather than only in the tool description.
	/// <para>
	/// The description is read when a tool is being chosen; this is read afterwards, by a caller that
	/// has since gone off to do something else, and it is the only place that can tell it nothing will
	/// arrive on its own. Saying so is the honest half of "hand the answer back as an event": pushing
	/// a completion where the caller can act on it needs <c>notifications/claude/channel</c>, which is
	/// client-specific rather than MCP, is in research preview behind an organisation policy, and is
	/// <em>dropped silently</em> where it is not enabled -- with no error to the server, so this tool
	/// cannot know whether a promise to notify would be kept. A promise that can be silently broken is
	/// worse than no promise, and the delivery is next-turn either way, which is what calling again
	/// already is.
	/// </para>
	/// <para>
	/// A notice rather than a field on <see cref="WorkspaceSummary"/>, because the tray window and
	/// <c>GET /admin/workspaces</c> read the same record through <c>Describe</c>, and "call this tool
	/// again" is advice to one caller rather than a fact about the workspace.
	/// </para>
	/// </summary>
	private const string StillLoadingNotice =
		"Still loading. Nothing will be pushed when it finishes: call rose_workspace_open again to "
			+ "check, or just ask the question you came for -- every other rose_* tool waits for the "
			+ "load by itself.";

	[McpServerTool(
		Name = ToolNames.WorkspaceStatus,
		Title = "Roslyn workspace status",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.WorkspaceStatus)]
	public async Task<WorkspaceStatusReport> StatusAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default)
	{
		var worker = await workspaces.GetOrStartAsync(WorkspaceHints.From(workspace), cancellationToken);
		return await workspaces.StatusOfAsync(worker, cancellationToken, progress);
	}

	[McpServerTool(
		Name = ToolNames.WorkspaceReload,
		Title = "Reload a workspace",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.WorkspaceReload)]
	public async Task<WorkspaceStatusReport> ReloadAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(WorkspaceHelp)] string? workspace = null,
		[Description("MSBuild configuration to load under, for example Debug-2027.")] string? configuration = null,
		[Description("MSBuild platform to load under, for example x64.")] string? platform = null,
		[Description("Further MSBuild properties, each as Name=Value.")] string[]? properties = null,
		CancellationToken cancellationToken = default)
	{
		var worker = await workspaces.RestartAsync(
			WorkspaceHints.From(workspace),
			cancellationToken,
			WorkspaceBuildOverrides.From(configuration, platform, properties));
		return await workspaces.StatusOfAsync(worker, cancellationToken, progress);
	}

	[McpServerTool(
		Name = ToolNames.WorkspaceClose,
		Title = "Close a workspace",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false)]
	[Description(ToolDescriptions.WorkspaceClose)]
	public async Task<string> CloseAsync(
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default)
	{
		var closed = await workspaces.CloseAsync(WorkspaceHints.From(workspace), cancellationToken);
		return closed ? "Workspace closed." : "That workspace was not open.";
	}
}
