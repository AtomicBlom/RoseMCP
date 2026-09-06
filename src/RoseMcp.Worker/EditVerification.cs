using System.Runtime.CompilerServices;

using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Compiles what an edit produced, and reports what it changed about the errors rather than what
/// errors there are.
/// <para>
/// The difference is the whole point. A project mid-refactor has errors already, and a list of them
/// answers a question nobody asked: an agent needs to know whether the edit it just made was sound,
/// and a hundred pre-existing errors bury the two that are its fault. Comparing before with after
/// costs a second compilation and turns the answer from a haystack into a sentence.
/// </para>
/// <para>
/// Which projects are compiled is <see cref="ScopeFor"/>'s decision, and it is bounded by what the
/// edit can reach rather than by the solution: compiling everything twice on every member edit would
/// cost more than the build this exists to avoid. Whatever it chose is named in the answer, and the
/// dependents a narrower scope left out are named too.
/// </para>
/// <para>
/// Analyzers run in the projects the edit actually wrote to, and only there. A repository that
/// escalates IDE0055 or IDE0005 to an error fails its build on a diagnostic no compiler pass
/// produces, so a verification without them would report clean on an edit that does not build --
/// and the rest of the scope would double the cost of the slowest thing this server does. The answer
/// says which projects had them.
/// </para>
/// </summary>
public static class EditVerification
{
	/// <summary>
	/// Compiles <paramref name="projects"/> before and after, and reports the difference.
	/// </summary>
	/// <param name="diagnostics">The shared analyser, so its cache is shared with rose_diagnostics.</param>
	/// <param name="before">The solution as it was.</param>
	/// <param name="after">The solution as the edit leaves it.</param>
	/// <param name="projects">Which projects to compile, by name.</param>
	/// <param name="nearest">
	/// The file the edit was aimed at, so its own errors are reported first. An error there is
	/// usually the cause and an error elsewhere usually the consequence.
	/// </param>
	/// <param name="cancellationToken">Cancels the compilations, which are the expensive part.</param>
	public static async Task<Verification> RunAsync(
		DiagnosticsService diagnostics,
		Solution before,
		Solution after,
		IReadOnlyList<string> projects,
		string? nearest,
		CancellationToken cancellationToken)
	{
		if (projects.Count == 0) return Verification.NotRun;

		var analyzed = ChangedProjects(before, after, projects);

		var was = await ErrorsAsync(diagnostics, before, projects, analyzed, cancellationToken);
		var now = await ErrorsAsync(diagnostics, after, projects, analyzed, cancellationToken);

		var (introduced, resolved) = Delta(was, now);
		var ordered = Ordered(introduced, nearest);

		return new Verification
		{
			Ran = true,
			Introduced = ordered,
			ResolvedCount = resolved,
			TotalCount = now.Count,
			Projects = projects,
			AnalyzedProjects = analyzed,
			Notices = [.. AnalyzerNotices(projects, analyzed)],

			// Asked here rather than by each write tool, so the one thing a caller wants next after
			// "this name does not resolve" arrives with the error rather than a call later.
			Suggestions = await MissingImports.SuggestAsync(
				new WorkspaceSnapshot { Solution = after, Revision = 0 }, ordered, cancellationToken),
		};
	}

	/// <summary>
	/// The projects in scope whose documents the edit actually changed, which is where its own analyzer
	/// diagnostics can appear.
	/// <para>
	/// Taken from the two solutions rather than from the path the caller named, because an edit is not
	/// always one file: a signature change writes to every override and implementation, and a move
	/// writes to two files that may be in two projects. Intersected with the scope so this never
	/// compiles a project the caller narrowed away.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> ChangedProjects(
		Solution before,
		Solution after,
		IReadOnlyList<string> projects)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);

		foreach (var change in after.GetChanges(before).GetProjectChanges())
		{
			var wrote = change.GetChangedDocuments().Any() || change.GetAddedDocuments().Any();

			if (wrote && after.GetProject(change.ProjectId) is { } project) names.Add(project.Name);
		}

		return [.. projects.Where(names.Contains).Order(StringComparer.Ordinal)];
	}

	/// <summary>
	/// What the caller has to know about the analyzer half, because the answer is worth less without
	/// it: a repository that escalates IDE0055 or IDE0005 to an error fails its build on a diagnostic
	/// no compiler pass produces, and "compiles clean" would be a confident answer to a narrower
	/// question than the one asked.
	/// </summary>
	private static IEnumerable<string> AnalyzerNotices(
		IReadOnlyList<string> projects,
		IReadOnlyList<string> analyzed)
	{
		if (analyzed.Count == 0)
		{
			yield return "Analyzers did not run, so a rule this repository escalates to an error -- IDE0055 on "
				+ "formatting, IDE0005 on an unused import -- would fail the build without appearing here.";

			yield break;
		}

		var rest = projects.Except(analyzed, StringComparer.Ordinal).ToArray();

		yield return rest.Length == 0
			? $"Analyzers ran in {string.Join(", ", analyzed)}, so a rule escalated to an error is included."
			: $"Analyzers ran in {string.Join(", ", analyzed)}, where the edit wrote. "
				+ $"{string.Join(", ", rest)} had the compiler only, so an analyzer rule broken there is not in this answer.";
	}

	/// <summary>
	/// The projects that hold a file, which is the right scope for a change that stays inside one
	/// member: nothing outside them can see a body change at all.
	/// </summary>
	public static IReadOnlyList<string> ProjectsHolding(Solution solution, string filePath) =>
		[
			.. solution.GetDocumentIdsWithFilePath(filePath)
				.Select(id => solution.GetProject(id.ProjectId))
				.OfType<Project>()
				.Select(project => project.Name)
				.Distinct(StringComparer.Ordinal)
				.Order(StringComparer.Ordinal),
		];

	/// <summary>
	/// The projects to compile for one edit, chosen from what the edit can reach.
	/// </summary>
	/// <param name="solution">The solution as the edit leaves it.</param>
	/// <param name="filePath">The edited file, whose own projects are always included.</param>
	/// <param name="reaches">
	/// The symbol the edit changes the shape of, or null when it changes no shape -- a body, whose
	/// signature is copied character for character and which nothing outside can see.
	/// </param>
	/// <param name="requested">What the caller asked for, or Auto to be told.</param>
	public static IReadOnlyList<string> ScopeFor(
		Solution solution,
		string filePath,
		ISymbol? reaches,
		VerifyScope requested)
	{
		if (requested == VerifyScope.Solution) return AllProjects(solution);

		var holding = ProjectsHolding(solution, filePath);

		if (requested == VerifyScope.File) return holding;

		var outward = requested == VerifyScope.Dependents || (reaches is not null && ReachesOutside(solution, reaches));

		return outward ? WithDependents(solution, holding) : holding;
	}

	/// <summary>
	/// The dependent projects a narrower scope left out although the edit can reach them, so a result
	/// can say what it did not check rather than reporting a clean edit that was only half looked at.
	/// Empty whenever the scope covers everything the edit reaches, which is what Auto always does.
	/// </summary>
	public static IReadOnlyList<string> SkippedDependents(
		Solution solution,
		string filePath,
		ISymbol? reaches,
		VerifyScope requested)
	{
		if (requested != VerifyScope.File || reaches is null || !ReachesOutside(solution, reaches)) return [];

		var holding = ProjectsHolding(solution, filePath);

		return [.. WithDependents(solution, holding).Except(holding, StringComparer.Ordinal)];
	}

	/// <summary>
	/// Those projects and everything that transitively references them.
	/// <para>
	/// Less expensive than the count of projects suggests, because the analyser caches per project
	/// against Roslyn's own dependent semantic version: the second of the two passes only recompiles
	/// what the change actually reached.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> WithDependents(Solution solution, IReadOnlyList<string> projects)
	{
		var graph = solution.GetProjectDependencyGraph();
		var names = new HashSet<string>(projects, StringComparer.Ordinal);

		foreach (var project in solution.Projects.Where(project => names.Contains(project.Name)).ToArray())
		{
			foreach (var id in graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id))
			{
				if (solution.GetProject(id) is { } dependent) names.Add(dependent.Name);
			}
		}

		return [.. names.Order(StringComparer.Ordinal)];
	}

	/// <summary>
	/// Whether a change to this symbol's shape can break code in another project.
	/// <para>
	/// Effective accessibility, not declared: a public member of an internal type is internal, and
	/// taking the declared value would widen the scope for most of a well-encapsulated solution.
	/// </para>
	/// <para>
	/// Internal reaches outward only where the assembly says so. This is read from the project's own
	/// InternalsVisibleTo attributes rather than assumed either way, because assuming it never reaches
	/// is wrong for every repository whose test project sees internals -- which is most of them -- and
	/// assuming it always does makes the wide scope the default for almost every edit.
	/// </para>
	/// </summary>
	private static bool ReachesOutside(Solution solution, ISymbol symbol)
	{
		var accessibility = Effective(symbol);

		if (accessibility is Accessibility.Public or Accessibility.Protected
			or Accessibility.ProtectedOrInternal)
		{
			return true;
		}

		if (accessibility is not (Accessibility.Internal or Accessibility.ProtectedAndInternal)) return false;

		return symbol.ContainingAssembly is { } assembly && HasFriends(assembly);
	}

	/// <summary>
	/// The narrowest accessibility on the way out: a public member of an internal type is internal, and
	/// a member of a private nested type is private however it is declared.
	/// </summary>
	private static Accessibility Effective(ISymbol symbol)
	{
		var narrowest = symbol.DeclaredAccessibility;

		for (var containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
		{
			if (containing.DeclaredAccessibility < narrowest) narrowest = containing.DeclaredAccessibility;
		}

		return narrowest;
	}

	/// <summary>Whether an assembly hands its internals to any other, which makes internal reach out.</summary>
	private static bool HasFriends(IAssemblySymbol assembly) =>
		assembly.GetAttributes().Any(attribute =>
			attribute.AttributeClass?.Name == nameof(InternalsVisibleToAttribute));

	/// <summary>
	/// Every project, which is the right scope for a change to a signature: a call site this missed
	/// is by definition somewhere it did not look, and that is the failure being designed out. It
	/// costs less than it sounds, because the analyser caches per project against Roslyn's own
	/// dependent semantic version -- so the second pass only recompiles what the change reached.
	/// </summary>
	public static IReadOnlyList<string> AllProjects(Solution solution) =>
		[.. solution.Projects.Select(project => project.Name).Order(StringComparer.Ordinal)];

	/// <summary>
	/// Errors only, and every one of them.
	/// <para>
	/// Errors only because a warning is not a broken edit -- except where the repository says it is,
	/// and there the compilation reports its warnings as errors already, so this needs no setting of
	/// its own to follow. That is what makes the analyzer pass worth running at this severity: a
	/// project that escalates IDE0055 or IDE0005 fails its build on one, and the compiler produces
	/// neither. Every one of them because the delta is computed from these two lists, and a list
	/// truncated at the usual two hundred would make the comparison say whatever the cut-off happened
	/// to drop.
	/// </para>
	/// <para>
	/// Analyzers run only in <paramref name="withAnalyzers"/>. The same set is used for both passes,
	/// so a diagnostic cannot appear in one and not the other for a reason that has nothing to do with
	/// the edit.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<DiagnosticEntry>> ErrorsAsync(
		DiagnosticsService diagnostics,
		Solution solution,
		IReadOnlyList<string> projects,
		IReadOnlyList<string> withAnalyzers,
		CancellationToken cancellationToken)
	{
		var snapshot = new WorkspaceSnapshot { Solution = solution, Revision = 0 };
		var collected = new List<DiagnosticEntry>();

		foreach (var project in projects)
		{
			var result = await diagnostics.AnalyseAsync(
				snapshot,
				new DiagnosticsRequest
				{
					Scope = DiagnosticScope.Project,
					Target = project,
					MinimumSeverity = DiagnosticSeverity.Error,
					IncludeAnalyzers = withAnalyzers.Contains(project, StringComparer.Ordinal),
					MaxResults = int.MaxValue,
				},
				cancellationToken);

			collected.AddRange(result.Diagnostics);
		}

		return collected;
	}

	/// <summary>
	/// Which errors are new, matched on everything except position.
	/// <para>
	/// Position is left out deliberately: an edit that adds three lines moves every diagnostic below
	/// it, and a key including the line would report each one as both resolved and introduced --
	/// turning a clean edit into a page of noise.
	/// </para>
	/// </summary>
	private static (IReadOnlyList<DiagnosticEntry> Introduced, int Resolved) Delta(
		IReadOnlyList<DiagnosticEntry> before,
		IReadOnlyList<DiagnosticEntry> after)
	{
		var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var entry in before)
		{
			remaining[Key(entry)] = remaining.GetValueOrDefault(Key(entry)) + 1;
		}

		var introduced = new List<DiagnosticEntry>();

		foreach (var entry in after)
		{
			var key = Key(entry);

			// Counted rather than matched, so two identical errors in one file are two errors: fixing
			// one of them is a real change and has to show as one.
			if (remaining.TryGetValue(key, out var count) && count > 0)
			{
				remaining[key] = count - 1;
				continue;
			}

			introduced.Add(entry);
		}

		return (introduced, remaining.Values.Sum());
	}

	/// <summary>
	/// The edited file first, when there is one. An error there is usually the cause and an error
	/// elsewhere usually the consequence, and a caller reading only the first line of the answer
	/// should get the cause.
	/// </summary>
	private static IReadOnlyList<DiagnosticEntry> Ordered(IReadOnlyList<DiagnosticEntry> introduced, string? nearest) =>
		[
			.. introduced
				.OrderByDescending(entry => nearest is not null && SamePath(entry.FilePath, nearest))
				.ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.Line),
		];

	private static string Key(DiagnosticEntry entry) => $"{entry.Id}|{entry.FilePath}|{entry.Message}";

	private static bool SamePath(string? left, string right) =>
		left is { Length: > 0 } && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
