using System.Collections.Concurrent;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

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
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var notices = new List<string>(snapshot.Notices);
		var projects = SelectProjects(snapshot.Solution, request);

		var collected = new List<DiagnosticEntry>();
		var analysed = 0;

		foreach (var project in projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// A whole-solution pass with analyzers on is the slowest thing this server does, and it
			// is worth being able to see which project it is stuck in.
			progress?.Report(
				$"Analysing {project.Name} ({analysed + 1}/{projects.Count})",
				100.0 * analysed / projects.Count);

			var diagnostics = await ForProjectAsync(project, request.IncludeAnalyzers, notices, cancellationToken);
			var generatedNames = await GeneratedPathsAsync(project, diagnostics, cancellationToken);

			foreach (var diagnostic in diagnostics)
			{
				if (diagnostic.Severity < request.MinimumSeverity) continue;
				if (!Matches(diagnostic, request)) continue;

				collected.Add(ToEntry(diagnostic, project.Name, generatedNames));
			}

			analysed++;
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

		var current = _cache.TryGetValue(project.Id, out var cached) && cached.Version == version
			? cached
			: null;

		if (current is not null)
		{
			if (!includeAnalyzers) return current.Compiler;
			if (current.Analyzer is { } already) return current.Compiler.AddRange(already);
		}

		Interlocked.Increment(ref _compilationsAnalysed);

		var compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
		{
			notices.Add($"Project {project.Name} produced no compilation and was skipped.");
			return [];
		}

		var compiler = current?.Compiler ?? compilation.GetDiagnostics(cancellationToken);

		// Analyzers already run for this version are kept rather than dropped, so a compiler-only
		// request passing through does not make the next request that wants them pay for the run again.
		var analyzer = includeAnalyzers
			? await RunAnalyzersAsync(project, compilation, notices, cancellationToken)
			: current?.Analyzer;

		_cache[project.Id] = new CacheEntry(version, compiler, analyzer);

		return includeAnalyzers && analyzer is { } ran ? compiler.AddRange(ran) : compiler;
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

	/// <summary>
	/// The projects a request covers.
	/// <para>
	/// A file or project name that matches nothing is refused rather than widened to the solution. The
	/// wide answer is the shape of failure this whole surface is built against: it comes back clean and
	/// complete, for a question fourteen projects larger than the one asked, and a caller who mistyped
	/// a path reads it as an answer about that path. A refusal naming what it looked for costs one call.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">Nothing in the solution matches the file or project named.</exception>
	private static IReadOnlyList<Project> SelectProjects(Solution solution, DiagnosticsRequest request)
	{
		if (request.Scope == DiagnosticScope.Solution || request.Target is not { Length: > 0 } target)
		{
			return [.. solution.Projects];
		}

		if (request.Scope == DiagnosticScope.Project)
		{
			var byName = solution.Projects
				.Where(project => string.Equals(project.Name, target, StringComparison.OrdinalIgnoreCase)
					|| PathMatches(project.FilePath, target))
				.ToArray();

			if (byName.Length > 0) return byName;

			throw new ArgumentException(
				$"No project in this solution is called '{target}'. It has "
					+ $"{string.Join(", ", solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal))}.");
		}

		// Document scope: analyse the projects that compile the file. A file shared by several projects,
		// or multi-targeted, legitimately belongs to more than one.
		var owners = solution.Projects
			.Where(project => project.Documents.Any(document => PathMatches(document.FilePath, target)))
			.ToArray();

		if (owners.Length > 0) return owners;

		throw new ArgumentException(
			$"No project in this solution compiles '{target}'. If the file is new it appears on the next call, "
				+ "once a project's globs include it; if another solution compiles it, pass workspace. Leave "
				+ "filePath off to analyse the whole solution.");
	}

	private static bool PathMatches(string? candidate, string target)
	{
		if (string.IsNullOrEmpty(candidate)) return false;

		return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// One project's diagnostics at one semantic version, with the two halves kept apart.
	/// <para>
	/// Apart because a compiler-only request must be answered without the analyzer half and without
	/// recomputing it. Answering it with a richer cached list makes the reply depend on what something
	/// else asked for earlier, and EditVerification computes a before-and-after delta from exactly
	/// these lists -- so one pass hitting such an entry and the other missing it reports analyzer
	/// errors as resolved by an edit that never touched them. Collapsing them into one list instead
	/// costs a full analyzer run every time a compiler-only request lands between two that want them.
	/// </para>
	/// </summary>
	private sealed record CacheEntry(
		VersionStamp Version,
		ImmutableArray<Diagnostic> Compiler,
		ImmutableArray<Diagnostic>? Analyzer);
}
