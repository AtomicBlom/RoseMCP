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
	/// <param name="usingsReach">
	/// The files the tool's own <c>usings</c> argument imports into, empty for a tool without one, so a
	/// suggested import names only something its caller can pass.
	/// </param>
	/// <param name="cancellationToken">Cancels the compilations, which are the expensive part.</param>
	public static async Task<Verification> RunAsync(
		DiagnosticsService diagnostics,
		Solution before,
		Solution after,
		IReadOnlyList<string> projects,
		string? nearest,
		IReadOnlyCollection<string> usingsReach,
		CancellationToken cancellationToken)
	{
		if (projects.Count == 0) return Verification.NotRun;

		var analyzed = ChangedProjects(before, after, projects);

		var was = await ErrorsAsync(diagnostics, before, projects, analyzed, cancellationToken);
		var now = await ErrorsAsync(diagnostics, after, projects, analyzed, cancellationToken);

		var movement = await TextMovement.BetweenAsync(before, after, cancellationToken);
		var (introduced, resolved) = DiagnosticDelta.Compare(was, now, movement);
		var ordered = Ordered(introduced, nearest);
		var unread = UnreadConfigs(before, after, analyzed);

		return new Verification
		{
			Ran = true,
			Introduced = ordered,
			ResolvedCount = resolved,
			TotalCount = now.Count,
			Projects = projects,
			AnalyzedProjects = analyzed,
			Notices = [.. AnalyzerNotices(projects, analyzed), .. unread.Select(Describe)],
			UnreadConfigs = [.. unread.Select(config => config.Config).Distinct(StringComparer.OrdinalIgnoreCase)],

			// Asked here rather than by each write tool, so the one thing a caller wants next after
			// "this name does not resolve" arrives with the error rather than a call later.
			Suggestions = await MissingImports.SuggestAsync(
				new WorkspaceSnapshot { Solution = after, Revision = 0 }, ordered, usingsReach, cancellationToken),
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
	/// The analyzer config files on disk that apply to a file the edit wrote in <paramref name="analyzed"/> and
	/// that its project was never given, each with the project and the files it applies to.
	/// <para>
	/// The load gives every project the files in and above its directory, so what is left is a folder that held
	/// no source when the project was built -- the design-time build walks up from the files it compiles, and
	/// found nothing to walk from there. A file written into it compiles under Roslyn's default severities, and
	/// the build, which reads that folder's .editorconfig once the file is in it, can fail on a rule this compile
	/// never applied. Asked once per directory, since every file in one has the same answer.
	/// </para>
	/// <para>
	/// A project that sets <c>DiscoverEditorConfigFiles</c> or <c>DiscoverGlobalAnalyzerConfigFiles</c> to false
	/// is named here too, although its build reads none of that kind: the solution does not carry what the
	/// project's evaluation said. That costs a caveat on a compile that was right, where leaving it out would cost
	/// a clean answer on one that was wrong.
	/// </para>
	/// </summary>
	private static IReadOnlyList<UnreadConfig> UnreadConfigs(Solution before, Solution after, IReadOnlyList<string> analyzed)
	{
		var asked = new Dictionary<(ProjectId, string), IReadOnlyList<string>>();
		var unread = new Dictionary<(string Project, string Config), List<string>>();

		foreach (var change in after.GetChanges(before).GetProjectChanges())
		{
			if (after.GetProject(change.ProjectId) is not { } project) continue;
			if (!analyzed.Contains(project.Name, StringComparer.Ordinal)) continue;

			foreach (var id in change.GetChangedDocuments().Concat(change.GetAddedDocuments()))
			{
				if (project.GetDocument(id)?.FilePath is not { Length: > 0 } path) continue;
				if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory) continue;

				if (!asked.TryGetValue((project.Id, directory), out var configs))
				{
					configs = EditorConfigFiles.NotGiven(project, path);
					asked[(project.Id, directory)] = configs;
				}

				foreach (var config in configs)
				{
					if (!unread.TryGetValue((project.Name, config), out var files)) unread[(project.Name, config)] = files = [];
					files.Add(Path.GetFileName(path));
				}
			}
		}

		return
		[
			.. unread
				.OrderBy(entry => entry.Key.Project, StringComparer.Ordinal)
				.ThenBy(entry => entry.Key.Config, StringComparer.OrdinalIgnoreCase)
				.Select(entry => new UnreadConfig(entry.Key.Project, entry.Key.Config, entry.Value)),
		];
	}

	/// <summary>
	/// What to say about one analyzer config file a project was never given: which files it applies to, that what
	/// it sets is missing from this answer, and what brings it in.
	/// </summary>
	private static string Describe(UnreadConfig unread)
	{
		var files = unread.Files.Count <= 3
			? string.Join(", ", unread.Files)
			: $"{string.Join(", ", unread.Files.Take(3))} and {unread.Files.Count - 3} more";

		return $"{unread.Config} applies to {files} and was never given to {unread.Project}, so the severities and "
			+ "options it sets are not in this answer, and a build that reads it can fail where this did not. The "
			+ "design-time build gives it once a source under it is on disk: rose_workspace_reload.";
	}

	/// <summary>An analyzer config file a project was never given, and the files the edit wrote that it applies to.</summary>
	private sealed record UnreadConfig(string Project, string Config, IReadOnlyList<string> Files);

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

	private static bool SamePath(string? left, string right) =>
		left is { Length: > 0 } && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
