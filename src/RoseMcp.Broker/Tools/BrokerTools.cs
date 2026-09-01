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
		"Path to a solution, project, or any file inside one. Optional when exactly one workspace is open.";

	private static readonly Dictionary<string, object?> EmptyArguments = [];

	[McpServerTool(
		Name = ToolNames.WorkspaceOpen,
		Title = "Open a solution",
		ReadOnly = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Loads a solution into a warm Roslyn host and keeps it loaded, so later calls cost nothing to
        set up. Restores first when restore output is missing. Returns per-project load state and,
        importantly, any reason the workspace is degraded -- most often a source generator whose
        assembly has not been built, which otherwise produces no generated code and no error.
        """)]
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
	[Description("""
        Reports what is loaded and whether its answers can be trusted: per-project load state,
        document and source-generated document counts, and any degraded-load reason. Check this
        first when diagnostics or generated code look wrong, because a degraded workspace returns
        plausible but incomplete results rather than errors.
        """)]
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
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Restarts the worker process for a workspace. Ordinary edits are picked up automatically and
        need no reload; this exists for the two cases that cannot be handled any other way. One is
        rebuilding an analyzer or source generator, since assembly loading is one-way and a process
        that loaded the old build can never see the new one. The other is loading under different
        MSBuild properties -- a configuration or platform is fixed when the workspace opens, so
        changing it is a restart. workspace_status reports which ones are in use and what else the
        solution declares.
        """)]
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
		Idempotent = true,
		OpenWorld = false)]
	[Description("Stops the worker for a workspace and releases the memory its solution was holding.")]
	public async Task<string> CloseAsync(
		[Description(WorkspaceHelp)] string? workspace = null,
		CancellationToken cancellationToken = default)
	{
		var closed = await workspaces.CloseAsync(workspace, cancellationToken);
		return closed ? "Workspace closed." : "That workspace was not open.";
	}
}
