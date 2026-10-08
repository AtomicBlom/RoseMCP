using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>What to analyse and how much of it to report.</summary>
public sealed record DiagnosticsRequest
{
	public DiagnosticScope Scope { get; init; } = DiagnosticScope.Solution;

	/// <summary>File path for document scope, project name or path for project scope.</summary>
	public string? Target { get; init; }

	public DiagnosticSeverity MinimumSeverity { get; init; } = DiagnosticSeverity.Warning;

	/// <summary>
	/// Analyzers are off by default because they are expensive -- a solution-wide analyzer pass can
	/// take minutes where the compiler pass takes seconds.
	/// </summary>
	public bool IncludeAnalyzers { get; init; }

	public int MaxResults { get; init; } = 200;

	/// <summary>Only diagnostics with this id, ignoring case; null for every id.</summary>
	public string? Id { get; init; }

	/// <summary>True for only diagnostics in source-generated code, false for only those in files.</summary>
	public bool? IsGenerated { get; init; }
}
