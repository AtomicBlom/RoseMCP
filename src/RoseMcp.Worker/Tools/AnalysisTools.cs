using System.ComponentModel;

using Microsoft.CodeAnalysis;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>Reading the solution: diagnostics and the generated code no file tool can reach.</summary>
[McpServerToolType]
public sealed class AnalysisTools(
	WorkspaceHost host,
	DiagnosticsService diagnostics,
	CodeFixCatalog codeFixes,
	SharedWorkProgress sharedWork)
{
	[McpServerTool(
		Name = ToolNames.ListCodeFixes,
		Title = "Code fixes available in a file",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ListCodeFixes)]
	public async Task<CodeFixList> ListCodeFixesAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.SingleFilePathArgument)] string filePath,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		return await CodeFixService.ListAsync(snapshot, codeFixes, filePath, cancellationToken, working);
	}

	[McpServerTool(
		Name = ToolNames.BuildFreshness,
		Title = "Is the build output newer than the sources",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.BuildFreshness)]
	public async Task<BuildFreshnessReport> BuildFreshnessAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectOrPathFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default)
	{
		// Comparing timestamps is instant. The only wait worth reporting is the workspace itself.
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);
		var freshness = BuildFreshness.Of(snapshot.Solution, project, cancellationToken);
		var stale = freshness.Count(candidate => candidate.Stale);

		var notices = new List<string>(snapshot.Notices);

		if (freshness.Count == 0 && !string.IsNullOrWhiteSpace(project))
		{
			notices.Add($"No project matched '{project}'.");
		}

		// Said out loud rather than left to be read off the list, because a caller asking this is about
		// to run something and the answer they need is one word.
		if (stale > 0)
		{
			notices.Add($"{stale} of {freshness.Count} project(s) would run as last built rather than as written.");
		}

		return new BuildFreshnessReport
		{
			Revision = snapshot.Revision,
			Projects = freshness,
			StaleCount = stale,
			Notices = notices,
		};
	}

	[McpServerTool(
		Name = ToolNames.Diagnostics,
		Title = "Roslyn diagnostics",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Diagnostics)]
	public async Task<DiagnosticsResult> DiagnosticsAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.DiagnosticFilePathArgument)] string? filePath = null,
		[Description(ToolDescriptions.DiagnosticProjectArgument)] string? project = null,
		[Description(ToolDescriptions.DiagnosticScopeArgument)] string? scope = null,
		[Description(ToolDescriptions.MinimumSeverityArgument)] string? minimumSeverity = null,
		[Description(ToolDescriptions.IncludeAnalyzersArgument)] bool includeAnalyzers = false,
		[Description(ToolDescriptions.MaxDiagnosticsArgument)] int maxResults = 200,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		// Read before the workspace, so a contradictory call is refused without paying for a load.
		var wanted = DiagnosticTarget.From(filePath, project, scope);

		var snapshot = await host.ReadAsync(cancellationToken);

		var request = new DiagnosticsRequest
		{
			Scope = wanted.Scope,
			Target = wanted.Target,
			MinimumSeverity = ParseSeverity(minimumSeverity),
			IncludeAnalyzers = includeAnalyzers,
			MaxResults = maxResults <= 0 ? 200 : maxResults,
		};

		return await diagnostics.AnalyseAsync(snapshot, request, cancellationToken, working);
	}

	[McpServerTool(
		Name = ToolNames.ListGeneratedDocuments,
		Title = "List source-generated documents",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ListGeneratedDocuments)]
	public async Task<GeneratedDocumentList> ListGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default)
	{
		var (waiting, working) = WorkProgress.Split(progress);
		using var following = sharedWork.Follow(waiting);

		var snapshot = await host.ReadAsync(cancellationToken);

		return await GeneratedDocumentService.ListAsync(
			snapshot, project, cancellationToken, working);
	}

	[McpServerTool(
		Name = ToolNames.ReadGeneratedDocument,
		Title = "Read a source-generated document",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.ReadGeneratedDocument)]
	public async Task<GeneratedDocumentContent> ReadGeneratedAsync(
		IProgress<ProgressNotificationValue> progress,
		[Description(ToolDescriptions.HintNameArgument)] string hintName,
		[Description(ToolDescriptions.ProjectFilterArgument)] string? project = null,
		CancellationToken cancellationToken = default)
	{
		// Reading one document is cheap; the only wait worth reporting is the workspace itself.
		using var following = sharedWork.Follow(WorkProgress.For(progress));

		var snapshot = await host.ReadAsync(cancellationToken);
		return await GeneratedDocumentService.ReadAsync(snapshot, hintName, project, cancellationToken);
	}

	private static DiagnosticSeverity ParseSeverity(string? severity) => severity?.ToLowerInvariant() switch
	{
		"hidden" => DiagnosticSeverity.Hidden,
		"info" or "information" => DiagnosticSeverity.Info,
		"error" => DiagnosticSeverity.Error,
		null or "" => DiagnosticSeverity.Warning,
		"warning" => DiagnosticSeverity.Warning,
		_ => throw ArgumentValues.Unknown("minimumSeverity", severity, "hidden", "info", "warning", "error"),
	};
}
