using System.ComponentModel;

using Microsoft.CodeAnalysis;

using ModelContextProtocol.Server;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker.Tools;

/// <summary>Reading the solution: diagnostics and the generated code no file tool can reach.</summary>
[McpServerToolType]
public sealed class AnalysisTools(WorkspaceHost host, DiagnosticsService diagnostics)
{
	[McpServerTool(
		Name = ToolNames.Diagnostics,
		Title = "Roslyn diagnostics",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Compiler (and optionally analyzer) diagnostics from a live Roslyn compilation, always
        computed against the current state of disk -- external edits made by other tools are picked
        up before the analysis runs, so results are never stale. Diagnostics originating in
        source-generated code are included and tagged with the hint name needed to read that code
        via roslyn_read_generated_document.
        """)]
	public async Task<DiagnosticsResult> DiagnosticsAsync(
		[Description("document, project, or solution. Defaults to solution.")] string? scope = null,
		[Description("File path for document scope, or project name for project scope.")] string? target = null,
		[Description("Lowest severity to report: hidden, info, warning, or error. Defaults to warning.")] string? minimumSeverity = null,
		[Description("Run analyzers as well as the compiler. Much slower over a whole solution; off by default.")] bool includeAnalyzers = false,
		[Description("Maximum diagnostics to return. Defaults to 200.")] int maxResults = 200,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await host.ReadAsync(cancellationToken);

		var request = new DiagnosticsRequest
		{
			Scope = ParseScope(scope),
			Target = target,
			MinimumSeverity = ParseSeverity(minimumSeverity),
			IncludeAnalyzers = includeAnalyzers,
			MaxResults = maxResults <= 0 ? 200 : maxResults,
		};

		return await diagnostics.AnalyseAsync(snapshot, request, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.ListGeneratedDocuments,
		Title = "List source-generated documents",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Lists the documents this solution's source generators produce. These files exist only inside
        the compilation and are not written to disk, so no file search or directory listing will
        find them. If the list is empty the notices explain whether the project has no generators or
        has generators that are failing to load.
        """)]
	public async Task<GeneratedDocumentList> ListGeneratedAsync(
		[Description("Limit to one project by name. Defaults to the whole solution.")] string? project = null,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await host.ReadAsync(cancellationToken);
		return await GeneratedDocumentService.ListAsync(snapshot, project, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.ReadGeneratedDocument,
		Title = "Read a source-generated document",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("""
        Returns the full text of one source-generated document, identified by the hint name from
        roslyn_list_generated_documents or from a diagnostic's generatedHintName. Use this whenever
        a diagnostic points at a file that does not exist on disk.
        """)]
	public async Task<GeneratedDocumentContent> ReadGeneratedAsync(
		[Description("Hint name of the generated document, for example Widget.Greeting.g.cs.")] string hintName,
		[Description("Limit to one project by name. Defaults to the whole solution.")] string? project = null,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await host.ReadAsync(cancellationToken);
		return await GeneratedDocumentService.ReadAsync(snapshot, hintName, project, cancellationToken);
	}

	private static DiagnosticScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
	{
		"document" or "file" => DiagnosticScope.Document,
		"project" => DiagnosticScope.Project,
		_ => DiagnosticScope.Solution,
	};

	private static DiagnosticSeverity ParseSeverity(string? severity) => severity?.ToLowerInvariant() switch
	{
		"hidden" => DiagnosticSeverity.Hidden,
		"info" or "information" => DiagnosticSeverity.Info,
		"error" => DiagnosticSeverity.Error,
		_ => DiagnosticSeverity.Warning,
	};
}
