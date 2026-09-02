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

	private static readonly Dictionary<string, object?> EmptyArguments = [];

	[McpServerTool(
		Name = ToolNames.WorkspaceOpen,
		Title = "Open a solution",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.WorkspaceOpen)]
	public async Task<WorkspaceStatusReport> OpenAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description("Path to a solution, project, directory, or any file inside one.")] string path,
		CancellationToken cancellationToken = default)
	{
		var worker = await workspaces.GetOrStartAsync(path, cancellationToken);
		return await worker.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, EmptyArguments, cancellationToken, progress);
	}

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
		var worker = await workspaces.GetOrStartAsync(workspace, cancellationToken);
		return await worker.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, EmptyArguments, cancellationToken, progress);
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
			workspace,
			cancellationToken,
			WorkspaceBuildOverrides.From(configuration, platform, properties));
		return await worker.CallAsync<WorkspaceStatusReport>(
			ToolNames.WorkspaceStatus, EmptyArguments, cancellationToken, progress);
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
		var closed = await workspaces.CloseAsync(workspace, cancellationToken);
		return closed ? "Workspace closed." : "That workspace was not open.";
	}
}
