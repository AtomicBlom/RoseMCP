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

	/// <summary>
	/// Leaves out the projects with no restore output whose errors are references that resolve to nothing,
	/// and names them in a notice instead. Only a solution-wide pass asks this: there two such projects
	/// bury every real error in the solution under tens of thousands that say only that restore did not
	/// run, and a project named on purpose still has them all listed.
	/// </summary>
	public bool WithholdUnrestored { get; init; }
}
