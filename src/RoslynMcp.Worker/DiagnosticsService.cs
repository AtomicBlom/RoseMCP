using System.Collections.Concurrent;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

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
}

/// <summary>
/// Computes diagnostics against an already-reconciled snapshot.
/// <para>
/// Because the compilation includes source-generated trees, errors originating in generated code
/// appear here with no extra work -- and are tagged with the hint name that reads them back, since
/// there is no file on disk for the caller to open.
/// </para>
/// </summary>
public sealed class DiagnosticsService(ILogger<DiagnosticsService> logger)
{
	/// <summary>
	/// Keyed on the project's dependent semantic version, which is Roslyn's own answer to "has
	/// anything that could change this project's meaning moved". Anything coarser re-analyses far
	/// too often; anything homegrown gets the transitive cases wrong.
	/// </summary>
	private readonly ConcurrentDictionary<ProjectId, CacheEntry> _cache = new();

	private int _compilationsAnalysed;

	/// <summary>
	/// How many times a project has actually been analysed rather than served from cache.
	/// Exposed so tests can prove the cache is doing its job; a cache that silently never hits
	/// looks identical from the outside.
	/// </summary>
	public int CompilationsAnalysed => Volatile.Read(ref _compilationsAnalysed);

	public async Task<DiagnosticsResult> AnalyseAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsRequest request,
		CancellationToken cancellationToken)
	{
		var notices = new List<string>(snapshot.Notices);
		var projects = SelectProjects(snapshot.Solution, request, notices);

		var collected = new List<DiagnosticEntry>();

		foreach (var project in projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var diagnostics = await ForProjectAsync(project, request.IncludeAnalyzers, notices, cancellationToken);
			var generatedNames = await GeneratedPathsAsync(project, diagnostics, cancellationToken);

			foreach (var diagnostic in diagnostics)
			{
				if (diagnostic.Severity < request.MinimumSeverity) continue;
				if (!Matches(diagnostic, request)) continue;

				collected.Add(ToEntry(diagnostic, project.Name, generatedNames));
			}
		}

		var ordered = collected
			.OrderByDescending(entry => entry.Severity, StringComparer.Ordinal)
			.ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(entry => entry.Line)
			.ToArray();

		var truncated = ordered.Length > request.MaxResults;
		if (truncated)
		{
			notices.Add($"Showing {request.MaxResults} of {ordered.Length} diagnostics. Narrow the scope or raise "
				+ "the minimum severity to see the rest.");
		}

		return new DiagnosticsResult
		{
			Revision = snapshot.Revision,
			Diagnostics = truncated ? ordered[..request.MaxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
			IncludedAnalyzers = request.IncludeAnalyzers,
			Notices = notices,
		};
	}

	private async Task<ImmutableArray<Diagnostic>> ForProjectAsync(
		Project project,
		bool includeAnalyzers,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var version = await project.GetDependentSemanticVersionAsync(cancellationToken);

		if (_cache.TryGetValue(project.Id, out var cached)
			&& cached.Version == version
			&& (cached.IncludedAnalyzers || !includeAnalyzers))
		{
			return cached.Diagnostics;
		}

		Interlocked.Increment(ref _compilationsAnalysed);

		var compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
		{
			notices.Add($"Project {project.Name} produced no compilation and was skipped.");
			return [];
		}

		var diagnostics = compilation.GetDiagnostics(cancellationToken);

		if (includeAnalyzers)
		{
			diagnostics = diagnostics.AddRange(await RunAnalyzersAsync(project, compilation, notices, cancellationToken));
		}

		_cache[project.Id] = new CacheEntry(version, includeAnalyzers, diagnostics);
		return diagnostics;
	}

	private async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
		Project project,
		Compilation compilation,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var analyzers = project.AnalyzerReferences
			.SelectMany(reference => SafeGetAnalyzers(reference, project.Language))
			.ToImmutableArray();

		if (analyzers.IsEmpty) return [];

		// A single throwing analyzer must not take down the whole request, so failures are collected
		// as notices and the rest of the run continues.
		var failures = new List<string>();
		var options = new CompilationWithAnalyzersOptions(
			project.AnalyzerOptions,
			onAnalyzerException: (exception, analyzer, _) => failures.Add($"{analyzer.GetType().Name}: {exception.Message}"),
			concurrentAnalysis: true,
			logAnalyzerExecutionTime: false);

		try
		{
			var withAnalyzers = compilation.WithAnalyzers(analyzers, options);
			var results = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);

			foreach (var failure in failures.Distinct())
			{
				notices.Add($"Analyzer failed in {project.Name}: {failure}");
			}

			return results;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			logger.LogWarning(exception, "Running analyzers for {Project} failed.", project.Name);
			notices.Add($"Analyzers could not run for {project.Name}: {exception.Message}");
			return [];
		}
	}

	private static IEnumerable<DiagnosticAnalyzer> SafeGetAnalyzers(AnalyzerReference reference, string language)
	{
		try
		{
			return reference.GetAnalyzers(language);
		}
		catch (Exception)
		{
			// An analyzer assembly that will not load is reported by workspace_status, not here.
			return [];
		}
	}

	/// <summary>
	/// Maps generated tree paths to hint names, but only when a diagnostic actually points at one.
	/// Enumerating generated documents forces the generators to run, which is not worth doing to
	/// annotate a result set that has nothing generated in it.
	/// </summary>
	private static async Task<IReadOnlyDictionary<string, string>> GeneratedPathsAsync(
		Project project,
		ImmutableArray<Diagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var anyMissingOnDisk = diagnostics.Any(diagnostic =>
			diagnostic.Location.SourceTree is { FilePath.Length: > 0 } tree && !File.Exists(tree.FilePath));

		if (!anyMissingOnDisk) return new Dictionary<string, string>();

		var generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
		return generated
			.Where(document => document.FilePath is { Length: > 0 })
			.ToDictionary(document => document.FilePath!, document => document.HintName, StringComparer.OrdinalIgnoreCase);
	}

	private static DiagnosticEntry ToEntry(
		Diagnostic diagnostic,
		string projectName,
		IReadOnlyDictionary<string, string> generatedNames)
	{
		var span = diagnostic.Location.GetLineSpan();
		var path = string.IsNullOrEmpty(span.Path) ? null : span.Path;

		return new DiagnosticEntry
		{
			Id = diagnostic.Id,
			Severity = diagnostic.Severity.ToString(),
			Message = diagnostic.GetMessage(),
			Project = projectName,
			FilePath = path,
			Line = span.StartLinePosition.Line + 1,
			Column = span.StartLinePosition.Character + 1,
			GeneratedHintName = path is not null && generatedNames.TryGetValue(path, out var hint) ? hint : null,
			HelpLink = string.IsNullOrEmpty(diagnostic.Descriptor.HelpLinkUri) ? null : diagnostic.Descriptor.HelpLinkUri,
		};
	}

	private static bool Matches(Diagnostic diagnostic, DiagnosticsRequest request)
	{
		if (request.Scope != DiagnosticScope.Document || request.Target is null) return true;

		var path = diagnostic.Location.GetLineSpan().Path;
		return !string.IsNullOrEmpty(path)
			&& string.Equals(Path.GetFullPath(path), Path.GetFullPath(request.Target), StringComparison.OrdinalIgnoreCase);
	}

	private static IReadOnlyList<Project> SelectProjects(Solution solution, DiagnosticsRequest request, List<string> notices)
	{
		if (request.Scope == DiagnosticScope.Solution) return [.. solution.Projects];

		if (string.IsNullOrWhiteSpace(request.Target))
		{
			notices.Add($"No target given for {request.Scope} scope; analysing the whole solution instead.");
			return [.. solution.Projects];
		}

		if (request.Scope == DiagnosticScope.Project)
		{
			var byName = solution.Projects
				.Where(project => string.Equals(project.Name, request.Target, StringComparison.OrdinalIgnoreCase)
					|| PathMatches(project.FilePath, request.Target))
				.ToArray();

			if (byName.Length > 0) return byName;

			notices.Add($"No project matched '{request.Target}'; analysing the whole solution instead.");
			return [.. solution.Projects];
		}

		// Document scope: analyse the projects that contain the file. A file shared by several
		// projects, or multi-targeted, legitimately belongs to more than one.
		var owners = solution.Projects
			.Where(project => project.Documents.Any(document => PathMatches(document.FilePath, request.Target)))
			.ToArray();

		if (owners.Length > 0) return owners;

		notices.Add($"No project contains '{request.Target}'; analysing the whole solution instead.");
		return [.. solution.Projects];
	}

	private static bool PathMatches(string? candidate, string target)
	{
		if (string.IsNullOrEmpty(candidate)) return false;

		return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
	}

	private sealed record CacheEntry(VersionStamp Version, bool IncludedAnalyzers, ImmutableArray<Diagnostic> Diagnostics);
}
