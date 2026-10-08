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
	/// One entry per project, good for exactly as long as its <c>CacheKey</c> still describes that
	/// project.
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

				var entry = ToEntry(diagnostic, project.Name, generatedNames);
				if (!Keeps(entry, request)) continue;

				collected.Add(entry);
			}

			analysed++;
		}

		var ordered = collected
			.OrderByDescending(entry => entry.Severity, StringComparer.Ordinal)
			.ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(entry => entry.Line)
			.ToArray();

		// Past the cap, the shape rather than the first few: which diagnostics come first is an accident of
		// severity and path order, and a sample read as the whole sends the caller to fix the wrong file.
		// Every group in the shape is a value one of the narrowing arguments takes, so the next call asks
		// a smaller question.
		var truncated = ordered.Length > request.MaxResults;
		if (truncated)
		{
			notices.Add($"{ordered.Length} diagnostics is more than maxResults ({request.MaxResults}), so their shape is "
				+ "given instead of the list. Narrow with id, project, filePath or isGenerated -- every group in the "
				+ $"shape is a value one of them takes -- or pass maxResults={ordered.Length} to list them all.");
		}

		return new DiagnosticsResult
		{
			Revision = snapshot.Revision,
			Diagnostics = truncated ? [] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
			Shape = truncated ? ShapeOf(ordered) : null,
			IncludedAnalyzers = request.IncludeAnalyzers,
			Notices = notices,
		};
	}

	/// <summary>How many files a shape names, the rest counted beside them; the most a caller narrows to one at a time.</summary>
	public const int NamedFiles = 10;

	/// <summary>How a set of diagnostics divides by id, project and file, each group keyed by the value its argument takes.</summary>
	public static DiagnosticShape ShapeOf(IReadOnlyCollection<DiagnosticEntry> diagnostics)
	{
		var files = FacetGroups.Of(
			diagnostics.Where(entry => entry.GeneratedHintName is null),
			entry => entry.FilePath,
			(file, inIt) => new DiagnosticFileCount { FilePath = file, Count = inIt.Count });

		return new DiagnosticShape
		{
			Total = diagnostics.Count,
			InGeneratedCode = diagnostics.Count(entry => entry.GeneratedHintName is not null),
			Ids = FacetGroups.Of(
				diagnostics,
				entry => entry.Id,
				(id, inIt) => new DiagnosticIdCount { Id = id, Severity = inIt[0].Severity, Count = inIt.Count }),
			Projects = FacetGroups.Of(
				diagnostics,
				entry => entry.Project,
				(project, inIt) => new DiagnosticProjectCount { Project = project, Count = inIt.Count }),
			Files = FacetGroups.Capped(files, NamedFiles),
			FileCount = files.Count,
		};
	}

	/// <summary>Whether a diagnostic satisfies the id and generated-code filters the caller gave.</summary>
	private static bool Keeps(DiagnosticEntry entry, DiagnosticsRequest request)
	{
		var idMatches = request.Id is null || string.Equals(entry.Id, request.Id, StringComparison.OrdinalIgnoreCase);
		var generatedMatches = request.IsGenerated is not { } generated || (entry.GeneratedHintName is not null) == generated;

		return idMatches && generatedMatches;
	}

	private async Task<ImmutableArray<Diagnostic>> ForProjectAsync(
		Project project,
		bool includeAnalyzers,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var key = new CacheKey(
			await project.GetDependentSemanticVersionAsync(cancellationToken),
			await project.GetLatestDocumentVersionAsync(cancellationToken));

		var current = _cache.TryGetValue(project.Id, out var cached) && cached.Key == key
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

		// Analyzers already run for this key are kept rather than dropped, so a compiler-only
		// request passing through does not make the next request that wants them pay for the run again.
		var analyzer = includeAnalyzers
			? await RunAnalyzersAsync(project, compilation, notices, cancellationToken)
			: current?.Analyzer;

		_cache[project.Id] = new CacheEntry(key, compiler, analyzer);

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
	/// A file or project name that matches nothing is refused rather than widened to the solution, the
	/// policy <see cref="ProjectNames"/> keeps for every tool: the wide answer comes back clean and
	/// complete, for a question many projects larger than the one asked, and a caller who mistyped a
	/// path reads it as an answer about that path. A refusal naming what it looked for costs one call.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">Nothing in the solution matches the file or project named.</exception>
	private static IReadOnlyList<Project> SelectProjects(Solution solution, DiagnosticsRequest request)
	{
		if (request.Scope == DiagnosticScope.Solution || request.Target is not { Length: > 0 } target)
		{
			return [.. solution.Projects];
		}

		if (request.Scope == DiagnosticScope.Project) return ProjectNames.Resolve(solution, target);

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
	/// What has to still hold for a project's cached diagnostics to be worth serving.
	/// <para>
	/// Two stamps, because neither one answers the question alone. <c>Declarations</c> is Roslyn's
	/// dependent semantic version, which moves when the consumable declarations of this project or of
	/// any project it references move, and is the only half that hears about a referenced project at
	/// all. It ignores method bodies by design, so a key resting on it alone goes on answering from
	/// the text as it stood before a body changed -- and answers with the project's whole previous
	/// result set rather than one stray entry, so the warning a fix just removed and the error an edit
	/// just introduced are equally invisible. <c>Text</c> is the version of this project's most
	/// recently modified document, which moves for any edit at all, and is what closes that hole.
	/// </para>
	/// <para>
	/// A change reaching the workspace as text is where this bites: a document updated from its
	/// syntax root carries a new declarations stamp whether or not the declarations moved, while one
	/// updated from text keeps the old stamp for a body-only change. Absorbing an edit from disk is
	/// the second kind, so every edit made outside this process lands exactly where the coarser stamp
	/// cannot see it.
	/// </para>
	/// <para>
	/// A body edit in a referenced project moves neither stamp. That is the right answer rather than
	/// the same hole one level out: a body is not consumable, so nothing this project compiles can
	/// depend on it.
	/// </para>
	/// </summary>
	private readonly record struct CacheKey(VersionStamp Declarations, VersionStamp Text);

	/// <summary>
	/// One project's diagnostics at one <c>CacheKey</c>, with the two halves kept apart.
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
		CacheKey Key,
		ImmutableArray<Diagnostic> Compiler,
		ImmutableArray<Diagnostic>? Analyzer);
}
