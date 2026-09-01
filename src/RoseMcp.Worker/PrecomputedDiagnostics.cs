using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace RoseMcp.Worker;

/// <summary>
/// Feeds a fix-all the diagnostics that were already computed, rather than letting it recompute them.
/// <para>
/// Roslyn's <see cref="FixAllContext"/> asks its provider for diagnostics whenever it needs them, and
/// the default implementation would run the analyzers again -- once per document in the worst case.
/// Running only the analyzers that report the id being fixed is what makes this affordable at all, so
/// that work is done once and handed over here.
/// </para>
/// </summary>
public sealed class PrecomputedDiagnostics(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
{
	public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
		Task.FromResult(InProject(project));

	public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
		// Project-level only: diagnostics with no source location, which is not what a code fix acts on.
		Task.FromResult(InProject(project).Where(diagnostic => diagnostic.Location.SourceTree is null));

	public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
		Task.FromResult(diagnostics.Where(diagnostic =>
			diagnostic.Location.SourceTree is { } tree
				&& string.Equals(tree.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)));

	private IEnumerable<Diagnostic> InProject(Project project)
	{
		var paths = project.Documents
			.Select(document => document.FilePath)
			.OfType<string>()
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		return diagnostics.Where(diagnostic =>
			diagnostic.Location.SourceTree is not { } tree || paths.Contains(tree.FilePath));
	}
}
