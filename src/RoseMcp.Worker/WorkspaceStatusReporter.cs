using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

using RoseMcp.Contracts;
using RoseMcp.Worker.Xaml;

namespace RoseMcp.Worker;

/// <summary>
/// Describes a solution snapshot: what loaded, what generators produced, and any reason the
/// answers cannot be trusted.
/// <para>
/// Kept separate from loading so status reflects the snapshot as it is now, not as it was when the
/// solution was first opened. After an hour of edits those are not the same thing.
/// </para>
/// </summary>
public static partial class WorkspaceStatusReporter
{
	public static async Task<WorkspaceStatusReport> DescribeAsync(
		Solution solution,
		string solutionPath,
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		RestoreReport? restore,
		IReadOnlyList<ProjectEvaluationFailure> evaluationFailures,
		long revision,
		double loadSeconds,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null,
		BuildProperties? build = null,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null)
	{
		var (projects, xamlReports) = await DescribeProjectsAsync(
			solution, analyzerLoader, cancellationToken, progress);

		// Read after the projects are described, not before: describing one is what asks its references
		// for their generators, and that is the moment an assembly that will not load says so.
		var analyzerFailures = analyzerLoader?.LoadFailures ?? [];

		var reasons = (IReadOnlyList<WorkspaceReason>)
			[.. CollectDegradedReasons(workspaceDiagnostics, projects, restore, build, analyzerFailures, evaluationFailures),
			 .. XamlHealth(xamlReports),
			 .. NoticesFor(build, solution, workspaceDiagnostics, evaluationFailures, cancellationToken)];

		var report = new WorkspaceStatusReport
		{
			SolutionPath = solutionPath,
			State = WorkspaceState.Loaded,
			Revision = revision,
			Projects = projects,
			ProjectCount = projects.Count,
			LoadDiagnostics = LoadDiagnosticSummary.Summarise(workspaceDiagnostics),
			LoadDiagnosticCount = workspaceDiagnostics.Count,
			DegradedReasons = [],
			AnalyzerLoadFailures = analyzerFailures,
			EvaluationFailures = evaluationFailures,
			BuildConfiguration = build?.Describe(),
			AvailableConfigurations = build?.Available.Configurations ?? [],
			Restore = restore,
			LoadSeconds = loadSeconds,
		};

		return WithReasons(report, reasons, []);
	}

	private static async Task<(IReadOnlyList<ProjectStatus> Statuses, IReadOnlyList<XamlProjectReport> Xaml)> DescribeProjectsAsync(
		Solution solution,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		var statuses = new List<ProjectStatus>(solution.ProjectIds.Count);
		var xamlReports = new List<XamlProjectReport>();
		var total = solution.ProjectIds.Count;

		foreach (var project in solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Reported before the work rather than after, because running a project's generators is
			// the slow part and naming it afterwards would credit it to the next project along.
			progress?.Report(
				$"Checking generated code: {project.Name} ({statuses.Count + 1}/{total})",
				total == 0 ? 100 : 100.0 * statuses.Count / total);

			var generators = project.AnalyzerReferences
				.SelectMany(reference => SafeGetGenerators(reference, project.Language, analyzerLoader))
				.ToArray();

			// Only pay for generator execution when there is a generator to run.
			var generated = generators.Length == 0
				? []
				: (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToArray();

			var xaml = await XamlStubReportReader.ReadAsync(generated, cancellationToken);

			// Markup no dialect was chosen for may still be compiled here, by its framework's own generator, which only
			// the finished compilation shows. The generators have just run, so this costs no second run.
			var undialected = xaml is { Dialect: null, UnstubbedClasses.Count: > 0 };
			if (undialected && await project.GetCompilationAsync(cancellationToken) is { } compilation)
			{
				var generatedFiles = generated.Select(document => document.FilePath).OfType<string>().ToArray();
				xaml = xaml! with { CompiledInWorkspaceBy = XamlCompiledElsewhere.Find(compilation, generatedFiles, xaml.UnstubbedClasses) };
			}

			if (xaml is not null) xamlReports.Add(new XamlProjectReport(project.Name, xaml));

			// The report is our own plumbing rather than something the project generates, so it is
			// not counted and, in GeneratedDocumentService, not listed either.
			var generatedCount = generated.Length - (xaml is null ? 0 : 1);

			statuses.Add(new ProjectStatus
			{
				Name = project.Name,
				FilePath = project.FilePath ?? string.Empty,
				TargetFramework = ReadTargetFramework(project),
				LoadedSuccessfully = LoadedSuccessfully(project),
				DocumentCount = project.DocumentIds.Count,
				AdditionalDocumentCount = project.AdditionalDocumentIds.Count,
				AnalyzerReferenceCount = project.AnalyzerReferences.Count,
				GeneratorCount = generators.Length,
				GeneratedDocumentCount = generatedCount,
				MissingAnalyzerOutputs = FindMissingAnalyzerOutputs(project),
				XamlMarkupCount = xaml?.MarkupFileCount ?? 0,
				XamlStubbedCount = xaml?.StubbedClassCount ?? 0,
				XamlDialect = xaml?.Dialect,
				UnresolvedXamlTypes = xaml?.UnresolvedTypes ?? [],
			});
		}

		return (statuses, xamlReports);
	}

	/// <summary>One project's XAML stub outcome, carried until every project's is in, so they fold together.</summary>
	public readonly record struct XamlProjectReport(string Project, XamlStubReport Xaml);

	/// <summary>
	/// What is wrong with the solution's XAML stubs, if anything: one reason per kind of wrongness,
	/// naming the projects it covers.
	/// <para>
	/// Successful stubbing is reported in the per-project counts rather than here: it is a caveat
	/// worth seeing, not a reason to call the whole workspace degraded, and marking every XAML
	/// solution degraded would empty that word of meaning. Being unable to stub, or having had to
	/// guess, is a different matter.
	/// </para>
	/// <para>
	/// Folded across projects rather than yielded per project. The three kinds have one cause and one
	/// remedy each, so a per-project list repeats the explanation once per project -- eight paragraphs
	/// on Drawboard's Pdf solution, all of them the same sentence about elements that will not bind.
	/// The types themselves are not repeated here at all: <see cref="ProjectStatus.UnresolvedXamlTypes"/>
	/// already carries every one of them, per project, so naming the projects is enough to reach them.
	/// </para>
	/// </summary>
	public static IEnumerable<string> XamlReasons(IReadOnlyList<XamlProjectReport> reports) =>
		XamlHealth(reports).Select(reason => reason.Text);

	/// <summary>
	/// The reasons <see cref="XamlReasons"/> words, each with its kind and the projects it names. Each may
	/// affect only the projects it names: elsewhere in the solution there is no markup to stub.
	/// </summary>
	public static IEnumerable<WorkspaceReason> XamlHealth(IReadOnlyList<XamlProjectReport> reports)
	{
		// Markup its framework's own generator compiles in the workspace needs no stand-in, so it leaves no false errors.
		var undialected = reports
			.Where(report => report.Xaml.Dialect is null && report.Xaml.MarkupFileCount > 0 && report.Xaml.CompiledInWorkspaceBy is null)
			.ToArray();

		if (undialected.Length > 0)
		{
			yield return WorkspaceReason.Of(
				ReasonKinds.XamlNoDialect,
				HealthEffect.MayAffectSomeProjects,
				$"{Count(undialected.Length, "project has", "projects have")} XAML files that no dialect "
					+ "could be chosen for, so nothing stood in for the markup compiler and their code-behind reports "
					+ $"errors that are not real: {Name(undialected.Select(Describe))}.",
				undialected.Select(report => report.Project));
		}

		var ambiguous = reports.Where(report => report.Xaml.DialectAmbiguous).ToArray();

		if (ambiguous.Length > 0)
		{
			yield return WorkspaceReason.Of(
				ReasonKinds.XamlDialectAmbiguous,
				HealthEffect.MayAffectSomeProjects,
				$"{Count(ambiguous.Length, "project references", "projects reference")} more than one XAML "
					+ $"framework, so the dialect their stubs were written as is a guess: {Name(ambiguous.Select(Describe))}.",
				ambiguous.Select(report => report.Project));
		}

		var unresolved = reports.Where(report => report.Xaml.UnresolvedTypes.Count > 0).ToArray();

		if (unresolved.Length > 0)
		{
			var total = unresolved.Sum(report => report.Xaml.UnresolvedTypes.Count);

			yield return WorkspaceReason.Of(
				ReasonKinds.XamlUnresolvedTypes,
				HealthEffect.MayAffectSomeProjects,
				$"{Count(total, "named XAML element", "named XAML elements")} across "
					+ $"{Count(unresolved.Length, "project", "projects")} have a type the project cannot see, so they "
					+ "have no field and will not bind: "
					+ $"{Name(unresolved.Select(report => $"{report.Project} ({report.Xaml.UnresolvedTypes.Count})"))}. "
					+ "Each project's unresolvedXamlTypes lists them. A project that resolves none of its package types "
					+ "is usually one that was never restored, so check restore before reading these as missing usings.",
				unresolved.Select(report => report.Project));
		}

		static string Describe(XamlProjectReport report) => $"{report.Project} ({report.Xaml.DialectReason})";
	}

	/// <summary>
	/// The one reason covering every analyzer assembly that would not load, or null when they all did.
	/// <para>
	/// Grouped by assembly, because the failure this catches most often is one generator arriving at
	/// several versions from several target packs and all but one of them losing. Listing the failures
	/// separately buries that: one file name carrying a count is the finding, and nine paragraphs that
	/// each look like an unrelated broken package are not.
	/// </para>
	/// <para>
	/// Public, and taking the failures rather than a loader, for the reason
	/// <see cref="LoadDiagnosticSummary.Fold"/> is: the fold is the part worth testing and it needs no
	/// workspace to exercise.
	/// </para>
	/// </summary>
	public static string? AnalyzerReason(IReadOnlyList<AnalyzerLoadFailure> failures)
	{
		if (failures.Count == 0) return null;

		var assemblies = failures
			.GroupBy(failure => failure.Assembly, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.Count() == 1 ? group.Key : $"{group.Key} (x{group.Count()})");

		return $"{Count(failures.Count, "analyzer assembly", "analyzer assemblies")} failed to load, so the "
			+ "analyzers and source generators inside them are producing nothing while MSBuild still passes them "
			+ $"to the compiler: {Name(assemblies)}. One file name carrying a count is several versions of one "
			+ "generator colliding, and only one of them loaded. Rebuild them, or check their dependencies and "
			+ "the Roslyn version they were built against, then reload. analyzerLoadFailures has each message.";
	}

	/// <summary>
	/// The one reason covering projects that name an SDK and that this worker's own MSBuild could not
	/// evaluate, or null when every such project evaluated.
	/// <para>
	/// Degrading, where the same failure in a legacy project is only a notice, because an SDK project is
	/// exactly what the SDK's own MSBuild exists to evaluate: when it cannot, either the project is broken or
	/// MSBuild in this process is. Grouped by message, because one cause failing every project is one message
	/// carrying a count, and a line per project would bury it.
	/// </para>
	/// <para>
	/// Said as the worker being broken only on the evidence that tells the two apart: every SDK project failed
	/// here, and the design-time build -- which runs in a build host of its own -- loaded every one of them. A
	/// broken import or a malformed <c>Directory.Build.props</c> fails both, and telling someone to restart the
	/// worker over that is wrong advice; legacy projects fail here by design, so they count toward neither side.
	/// </para>
	/// </summary>
	/// <param name="failures">Every project the worker's own MSBuild could not evaluate.</param>
	/// <param name="projectCount">How many distinct project files the solution loaded.</param>
	/// <param name="loadedProjects">The project files the design-time build loaded successfully.</param>
	public static string? EvaluationReason(
		IReadOnlyList<ProjectEvaluationFailure> failures,
		int projectCount,
		IReadOnlySet<string> loadedProjects)
	{
		var sdk = failures.Where(failure => failure.NamesSdk).ToArray();
		if (sdk.Length == 0) return null;

		var messages = Grouped(sdk);

		if (BlamesTheWorker(failures, projectCount, loadedProjects))
		{
			return $"This worker's own MSBuild could not evaluate any of the solution's {Count(sdk.Length, "SDK project", "SDK projects")}, "
				+ "though the design-time build, in a process of its own, loaded every one of them. That says this worker's "
				+ $"MSBuild is broken -- usually an SDK it can no longer resolve -- rather than that the projects are: {messages}. "
				+ "What they import is unknown, so every .props or .targets change reloads the whole solution, and a worker in this "
				+ "state cannot be relied on for anything that resolves through MSBuild or loads an assembly afterwards. Nothing in "
				+ "the solution fixes this: rose_workspace_reload starts a fresh worker. evaluationFailures has each project's message.";
		}

		return $"{Count(sdk.Length, "project that names an SDK", "projects that name an SDK")} could not be evaluated by this "
			+ $"worker's own MSBuild: {messages}. What they import is unknown here, so every .props or .targets change reloads "
			+ "the whole solution. If the message names something in the project or a build file it imports, fix that; if the "
			+ "design-time build loaded the project anyway, MSBuild in this process is going wrong, and rose_workspace_reload "
			+ "starts a fresh worker. evaluationFailures has each project's message.";
	}

	/// <summary>
	/// Whether the evidence says this worker's MSBuild is broken rather than the projects: every SDK project
	/// failed here, and the design-time build loaded every one of them. Which of <see cref="EvaluationReason"/>'s
	/// two readings applies, and so which kind the reason is.
	/// </summary>
	private static bool BlamesTheWorker(
		IReadOnlyList<ProjectEvaluationFailure> failures,
		int projectCount,
		IReadOnlySet<string> loadedProjects)
	{
		var sdk = failures.Where(failure => failure.NamesSdk).ToArray();
		var sdkProjects = projectCount - (failures.Count - sdk.Length);
		var everySdkProjectFailed = sdk.Length >= sdkProjects;
		var buildHostLoadedThem = sdk.All(failure => loadedProjects.Contains(failure.Project));

		return sdk.Length > 0 && everySdkProjectFailed && buildHostLoadedThem;
	}

	/// <summary>
	/// The one notice covering projects that name no SDK and could not be evaluated, or null when there are none.
	/// <para>
	/// Not degrading: a legacy project's targets ship only with Visual Studio's MSBuild, which the design-time build
	/// uses and this evaluation cannot, so its failure here is expected and its answers are as good as any.
	/// </para>
	/// </summary>
	public static string? EvaluationNotice(IReadOnlyList<ProjectEvaluationFailure> failures)
	{
		var legacy = failures.Where(failure => !failure.NamesSdk).ToArray();
		if (legacy.Length == 0) return null;

		return $"{Count(legacy.Length, "project that names no SDK", "projects that name no SDK")} could not be evaluated "
			+ $"by this worker's own MSBuild: {Name(legacy.Select(ProjectName))}. That is expected of a legacy project, whose "
			+ "targets ship with Visual Studio's MSBuild, which the design-time build uses, so it does not make the workspace "
			+ "degraded. What they import is unknown here, so every .props or .targets change reloads the whole solution. "
			+ "evaluationFailures has each project's message.";
	}

	/// <summary>
	/// The one reason covering assemblies tool calls failed to load for this worker's own code, or null when
	/// none have.
	/// <para>
	/// Grouped by assembly, each naming the tools that failed on it, because one assembly is one broken path
	/// through the worker and the tools are how a caller recognises which of its answers are missing. Without
	/// this the workspace goes on reporting itself healthy while two tools fail on every call, and the only
	/// way to find out is to be the next caller.
	/// </para>
	/// </summary>
	public static string? AssemblyLoadReason(IReadOnlyList<AssemblyLoadFault> faults)
	{
		if (faults.Count == 0) return null;

		var assemblies = faults
			.GroupBy(fault => fault.Assembly, StringComparer.OrdinalIgnoreCase)
			.Select(group => $"{group.Key} ({string.Join(", ", group.Select(fault => fault.Tool).Distinct(StringComparer.Ordinal))})")
			.ToArray();

		return $"This worker could not load {Count(assemblies.Length, "assembly", "assemblies")} its own code needs, so the "
			+ $"tools that reach that code fail on every call while the rest answer: {Name(assemblies)}. "
			+ (faults.Any(fault => fault.RuntimeDirectoryMissing) ? AssemblyLoadFault.RuntimeGone : string.Empty)
			+ "A running process does not recover from this and nothing in the solution causes it: rose_workspace_reload "
			+ "starts a fresh worker.";
	}

	/// <summary>
	/// The one notice a read carries from a workspace that is degraded, or null when nothing degrades it.
	/// <para>
	/// A read cannot report what its workspace could not see: a project whose design-time build failed
	/// resolves nothing and so reports nothing, and a generator that did not load writes nothing for the
	/// compiler to complain about. An empty list from a degraded workspace is therefore not the fact an empty
	/// list from a healthy one is, and without this the two read the same. One line rather than the reasons,
	/// because it rides on every read and the reasons, each with its fix, are what status is for: the first
	/// reason's opening sentence is the why, since every reason leads with what is wrong and ends with the
	/// remedy.
	/// </para>
	/// <para>
	/// The why is held to <see cref="MaxDegradedWhy"/> characters. An opening sentence can quote what MSBuild
	/// said for every project that failed, and a quote or bracket left open in one of those messages leaves no
	/// full stop to find, so unbounded the line would grow with the solution's failures on every read and every
	/// batch -- the volume that gets a notice read past.
	/// </para>
	/// </summary>
	/// <param name="reasons">Why the workspace is degraded, as status would list them; empty when it is not.</param>
	public static string? DegradedNotice(IReadOnlyList<string> reasons) => Notice([.. reasons.Select(FirstSentence)]);

	/// <summary>
	/// The one notice a read carries from a degraded workspace, leaving out what the caller has been told
	/// not to hear: a reason the person accepted saying nothing, or one the calling session acknowledged, is
	/// not said; one the person accepted with a note is said in the person's words. Null when nothing is left
	/// to say, which is the workspace reading as healthy to this caller and to nobody else.
	/// <para>
	/// A reason is left out or reworded only where <paramref name="heard"/> covers its kind for every project
	/// it names, so a new kind of reason, or a project newly failing in an accepted kind, is said as it always
	/// is. The ones nobody has accepted lead, since they are the ones the caller knows nothing about.
	/// </para>
	/// </summary>
	/// <param name="reasons">Every reason in the workspace's health; notes are passed over, as they never degrade it.</param>
	/// <param name="heard">What the broker sent about how this call hears each kind and project.</param>
	public static string? DegradedNotice(IReadOnlyList<WorkspaceReason> reasons, IReadOnlyList<ReasonHearing> heard)
	{
		var unaccepted = new List<string>();
		var accepted = new List<string>();

		foreach (var reason in reasons.Where(reason => reason.Degrades))
		{
			if (HearingOf(reason, heard) is not { } hearing)
			{
				unaccepted.Add(FirstSentence(reason.Text));
				continue;
			}

			switch (hearing.Read)
			{
				case AgentsRead.Nothing:
					break;
				case AgentsRead.Note when !string.IsNullOrWhiteSpace(hearing.Note):
					accepted.Add($"A known reason, in the words of the person who accepted it: {hearing.Note.Trim()}");
					break;
				default:
					accepted.Add(FirstSentence(reason.Text));
					break;
			}
		}

		return Notice([.. unaccepted, .. accepted]);
	}

	/// <summary>
	/// How <paramref name="heard"/> says to hear <paramref name="reason"/>, or null where it does not cover the
	/// reason's kind for every project the reason names. Where it covers them differently, the most a caller
	/// is owed wins: Rose's line over a note, and a note over nothing.
	/// </summary>
	public static ReasonHearing? HearingOf(WorkspaceReason reason, IReadOnlyList<ReasonHearing> heard)
	{
		var covering = new List<ReasonHearing>();

		foreach (var project in reason.Keys)
		{
			var match = heard.FirstOrDefault(hearing =>
				string.Equals(hearing.Kind, reason.Kind, StringComparison.Ordinal)
				&& string.Equals(hearing.Covering, project, StringComparison.OrdinalIgnoreCase));

			if (match is null) return null;

			covering.Add(match);
		}

		return covering.FirstOrDefault(hearing => hearing.Read == AgentsRead.RoseLine)
			?? covering.FirstOrDefault(hearing => hearing.Read == AgentsRead.Note)
			?? covering[0];
	}

	/// <summary>The notice for the reasons' opening sentences, the first in full and the rest counted; null for none.</summary>
	private static string? Notice(IReadOnlyList<string> whys)
	{
		if (whys.Count == 0) return null;

		var others = whys.Count == 1 ? string.Empty : $" {Count(whys.Count - 1, "more reason", "more reasons")} besides.";

		return "This workspace is degraded, so this answer can be missing what it could not see rather than reporting it: "
			+ $"{Bounded(whys[0], MaxDegradedWhy)}{others} Ask rose_workspace_status for every reason and its fix.";
	}

	/// <summary>Rose's line for a reason: its opening sentence, cut to the ceiling a read's notice holds it to.</summary>
	public static string LineOf(string reason) => Bounded(FirstSentence(reason), MaxDegradedWhy);

	/// <summary>
	/// The shape each reason is reported with, in the order given, each with an id that is unique in the list:
	/// the kind, then the project where there is one, then a count of the others, and a suffix where two
	/// reasons would share an id.
	/// </summary>
	public static IReadOnlyList<HealthReason> HealthOf(IReadOnlyList<WorkspaceReason> reasons)
	{
		var used = new Dictionary<string, int>(StringComparer.Ordinal);
		var health = new List<HealthReason>(reasons.Count);

		foreach (var reason in reasons)
		{
			var id = reason.Projects.Count switch
			{
				0 => reason.Kind,
				1 => $"{reason.Kind}:{reason.Projects[0]}",
				_ => $"{reason.Kind}:{reason.Projects[0]}+{reason.Projects.Count - 1}",
			};

			var seen = used.GetValueOrDefault(id);
			used[id] = seen + 1;

			health.Add(new HealthReason
			{
				Id = seen == 0 ? id : $"{id}#{seen + 1}",
				Kind = reason.Kind,
				Effect = reason.Effect,
				Projects = reason.Projects,
				Subjects = reason.Subjects,
				Line = LineOf(reason.Text),
			});
		}

		return health;
	}

	/// <summary>
	/// <paramref name="report"/> describing <paramref name="reasons"/>: the degrading ones as its degraded
	/// reasons, the notes as its leading notices with <paramref name="otherNotices"/> after them, all of them in
	/// its health in that order, and its state from whether any degrades. The one place a report's three views
	/// of its reasons are written, which is what lets <see cref="ReasonsOf"/> read them back.
	/// </summary>
	public static WorkspaceStatusReport WithReasons(
		WorkspaceStatusReport report,
		IReadOnlyList<WorkspaceReason> reasons,
		IReadOnlyList<string> otherNotices)
	{
		var degrading = reasons.Where(reason => reason.Degrades).ToArray();
		var notes = reasons.Where(reason => !reason.Degrades).ToArray();

		return report with
		{
			State = degrading.Length == 0 ? WorkspaceState.Loaded : WorkspaceState.Degraded,
			DegradedReasons = [.. degrading.Select(reason => reason.Text)],
			Notices = [.. notes.Select(reason => reason.Text), .. otherNotices],
			Health = HealthOf([.. degrading, .. notes]),
		};
	}

	/// <summary>
	/// The reasons a report written by <see cref="WithReasons"/> describes, its health paired back with the
	/// text each entry stands for: the degraded reasons, then the leading notices.
	/// </summary>
	public static IReadOnlyList<WorkspaceReason> ReasonsOf(WorkspaceStatusReport report)
	{
		var texts = report.DegradedReasons.Concat(report.Notices).ToArray();

		return [.. report.Health
			.Take(texts.Length)
			.Select((health, index) => new WorkspaceReason(health.Kind, health.Effect, health.Projects, texts[index]) { Subjects = health.Subjects })];
	}

	/// <summary>The reason <see cref="AssemblyLoadReason"/> words, as one blocking every tool that reaches the code; null for no faults.</summary>
	public static WorkspaceReason? AssemblyLoadHealth(IReadOnlyList<AssemblyLoadFault> faults) =>
		AssemblyLoadReason(faults) is { } text ? WorkspaceReason.Of(ReasonKinds.AssemblyLoadFault, HealthEffect.BlocksAnswers, text, subjects: faults.Select(fault => fault.Assembly)) : null;

	/// <summary>
	/// The most of a reason a read's degraded notice repeats. Enough for every reason's opening sentence that
	/// names its cause and a few of its projects, which is what the folded reasons are written to fit; past
	/// it, the sentence is quoting MSBuild, and status has the quote.
	/// </summary>
	public const int MaxDegradedWhy = 240;

	/// <summary>
	/// <paramref name="text"/> cut to at most <paramref name="max"/> characters at a word boundary, an ellipsis
	/// saying it was cut, or the text itself where it fits.
	/// </summary>
	private static string Bounded(string text, int max)
	{
		if (text.Length <= max) return text;

		const string Ellipsis = "...";
		var room = max - Ellipsis.Length;
		var space = text.LastIndexOf(' ', room);
		var cut = space > room / 2 ? space : room;

		return $"{text[..cut].TrimEnd(' ', ',', ';', ':')}{Ellipsis}";
	}

	/// <summary>
	/// A reason's opening sentence, ended with a full stop. A full stop inside parentheses or double quotes
	/// does not end it, since that is where a reason quotes MSBuild or names a dialect's own explanation.
	/// </summary>
	private static string FirstSentence(string reason)
	{
		var depth = 0;
		var quoted = false;

		for (var index = 0; index < reason.Length; index++)
		{
			switch (reason[index])
			{
				case '(':
					depth++;
					break;
				case ')':
					depth = Math.Max(0, depth - 1);
					break;
				case '"':
					quoted = !quoted;
					break;
				case '.' when depth == 0 && !quoted && (index + 1 == reason.Length || reason[index + 1] == ' '):
					return reason[..(index + 1)];
			}
		}

		var trimmed = reason.TrimEnd();

		return trimmed.EndsWith('.') ? trimmed : $"{trimmed}.";
	}

	/// <summary>
	/// Failures grouped by what MSBuild said, the commonest first, each naming a few of its projects.
	/// </summary>
	private static string Grouped(IEnumerable<ProjectEvaluationFailure> failures) =>
		string.Join("; ", failures
			.GroupBy(failure => failure.Message, StringComparer.Ordinal)
			.OrderByDescending(group => group.Count())
			.Select(group => $"\"{group.Key}\" ({Count(group.Count(), "project", "projects")}: {Name(group.Select(ProjectName))})"));

	/// <summary>A project file's name without its directory or extension, which is how a project is named everywhere else.</summary>
	private static string ProjectName(ProjectEvaluationFailure failure) =>
		Path.GetFileNameWithoutExtension(failure.Project);

	/// <summary>
	/// The one reason covering projects left with no restore output, or null when none were.
	/// <para>
	/// Separate from the restore-failed reason and reached only when that one did not fire: a restore
	/// that failed explains itself and its output is the actionable part. This is the other case, and
	/// the one worth having -- restore reporting success while most of the solution is unrestored,
	/// which is what <c>dotnet restore</c> does to a solution of non-SDK projects.
	/// </para>
	/// </summary>
	public static string? UnrestoredReason(RestoreReport? restore)
	{
		if (restore?.Unrestored is not { Count: > 0 } unrestored) return null;

		var lead = restore.Ran ? "Restore reported success, but" : "Restore did not run, and";

		return $"{lead} {Count(unrestored.Count, "project has", "projects have")} no restore output: "
			+ $"{Name(unrestored)}. Their package references resolve to nothing, so the types, analyzers and "
			+ "generators those packages carry are all absent, and the errors that follow describe everything "
			+ "except the cause. 'dotnet restore' passes over projects it does not understand -- non-SDK csproj, "
			+ "which is every UWP one -- and exits 0 regardless; restore those with MSBuild. restore.unrestored "
			+ "lists them.";
	}

	/// <summary>
	/// Names a few of something and says how many were left out, so a folded reason stays actionable
	/// without growing back into the list it replaced. Three, because that is enough to recognise a
	/// family by and short enough to read in a tray card.
	/// </summary>
	private static string Name(IEnumerable<string> items, int show = 3)
	{
		var all = items.ToArray();
		var named = string.Join(", ", all.Take(show));

		return all.Length > show ? $"{named}, and {all.Length - show} more" : named;
	}

	/// <summary>
	/// A count against its noun, both spellings given. English pluralisation is not a suffix rule --
	/// "analyzer assemblies" and "projects have" do not come from adding s -- and a reason that says
	/// "1 projects have" reads as a bug in the thing reporting the bug.
	/// </summary>
	private static string Count(int count, string singular, string plural) =>
		$"{count} {(count == 1 ? singular : plural)}";

	/// <summary>
	/// A generator built against a Roslyn this worker does not have, or one with a broken dependency,
	/// throws on load. That is a fact about the solution rather than a reason to fail the whole open,
	/// so it is recorded beside the failures the reference raises through its own event: a reference
	/// that yields no generators and says nothing is the silent nothing this project exists to catch.
	/// </summary>
	private static IEnumerable<ISourceGenerator> SafeGetGenerators(
		AnalyzerReference reference,
		string language,
		ShadowCopyAnalyzerAssemblyLoader? analyzerLoader)
	{
		try
		{
			return reference.GetGenerators(language);
		}
		catch (Exception exception)
		{
			analyzerLoader?.RecordLoadFailure(
				reference.FullPath ?? reference.Display ?? language, exception.Message);

			return [];
		}
	}

	/// <summary>
	/// Whether this project's semantic results can be trusted. Roslyn keeps the real answer to itself
	/// -- Project.HasSuccessfullyLoadedAsync is internal -- so it is inferred from what is public: a
	/// project whose design-time build fell over resolves no metadata references at all, not even the
	/// framework.
	/// <para>
	/// Asked of the compilation, and deliberately not of what MSBuild said about it. MSBuild raises a
	/// <see cref="WorkspaceDiagnosticKind.Failure"/> when NuGet's vulnerability audit cannot reach its
	/// feed, which names every project it could not audit while saying nothing about whether any of
	/// them compiled -- on a machine without that feed, it names all of them. Blaming a project for
	/// appearing in one marked 27 of the 37 projects in Shared.slnx as failed when every one of them
	/// had resolved its references and loaded its documents.
	/// </para>
	/// <para>
	/// A project that resolved no references resolved nothing, and that is the condition worth
	/// reporting. The complaints themselves are not lost: they are in <c>loadDiagnostics</c>.
	/// </para>
	/// </summary>
	private static bool LoadedSuccessfully(Project project) => project.MetadataReferences.Count > 0;

	/// <summary>
	/// The framework a project was actually loaded for.
	/// <para>
	/// Null is meant to mean something here: a project that resolved no framework is the signature of
	/// a solution loaded under a configuration it does not declare, the failure that yields thousands
	/// of diagnostics about System.Object being undefined. Anything that makes null common empties
	/// that signal, so this asks three sources before giving up.
	/// </para>
	/// <para>
	/// The build's own value first, which is exact and carries the platform:
	/// <c>net10.0-windows10.0.26100.0</c>. It only reaches the analyzer config when something asked
	/// for it though, so eleven healthy netstandard2.0 projects in one Revit monorepo have
	/// none. Then the preprocessor symbols, which the SDK defines unconditionally and which no
	/// generator has to request. The project name last, and only when it looks like a framework:
	/// Roslyn appends a TFM there solely to tell the targets of a multi-targeted project apart, so a
	/// project merely called <c>Foo (Legacy)</c> would otherwise answer with "Legacy".
	/// </para>
	/// </summary>
	private static string? ReadTargetFramework(Project project)
	{
		var declared = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
		if (declared.TryGetValue("build_property.TargetFramework", out var framework)
			&& !string.IsNullOrWhiteSpace(framework))
		{
			return framework;
		}

		if (TargetFrameworkSymbols.Infer(project.ParseOptions?.PreprocessorSymbolNames) is { } inferred) return inferred;

		var open = project.Name.LastIndexOf('(');
		var close = project.Name.LastIndexOf(')');
		if (open < 0 || close < open) return null;

		var named = project.Name[(open + 1)..close];

		return named.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? named : null;
	}

	/// <summary>
	/// Names of analyzer assemblies this project expects but which are not on disk.
	/// <para>
	/// Checking the file rather than the reference list is the whole point. MSBuild puts the
	/// expected output of an unbuilt in-solution generator on the /analyzer: line regardless, so
	/// the reference is present and only the file is missing. Roslyn loads no generators from it
	/// and reports no error, which is exactly how this failure stays invisible.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> FindMissingAnalyzerOutputs(Project project)
	{
		return project.AnalyzerReferences
			.Select(reference => reference.FullPath)
			.Where(path => !string.IsNullOrEmpty(path) && !File.Exists(path))
			.Select(Path.GetFileNameWithoutExtension)
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <summary>
	/// What is worth saying about a workspace without calling it degraded.
	/// <para>
	/// Stale build output belongs here and not in degradedReasons, and the distinction is the point.
	/// Degraded means these answers cannot be trusted, and they are exactly as good with a stale bin
	/// directory as without one -- this reads source, not assemblies. It is also the ordinary state
	/// of a solution somebody is editing, so putting it in degradedReasons would mark almost every
	/// workspace on the machine degraded: the same emptying of the word that the blanket
	/// MSBuild-failure count below was already narrowed to avoid.
	/// </para>
	/// <para>
	/// It is still worth saying, because the thing it warns about does not present as a build
	/// failure. It presents as a test failing for a reason that has nothing to do with the change.
	/// </para>
	/// <para>
	/// A reference left unresolved because its project has not been built is a notice for the same
	/// reason: the projects still load, and the remedy is a build rather than anything about trust.
	/// </para>
	/// </summary>
	private static IEnumerable<WorkspaceReason> NoticesFor(
		BuildProperties? build,
		Solution solution,
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		IReadOnlyList<ProjectEvaluationFailure> evaluationFailures,
		CancellationToken cancellationToken)
	{
		if (build?.Notice is { } notice) yield return WorkspaceReason.Of(ReasonKinds.ConfigurationChosen, HealthEffect.Note, notice);

		if (EvaluationNotice(evaluationFailures) is { } unevaluated)
		{
			yield return WorkspaceReason.Of(
				ReasonKinds.LegacyProjectNotEvaluated,
				HealthEffect.Note,
				unevaluated,
				evaluationFailures.Where(failure => !failure.NamesSdk).Select(ProjectName));
		}

		var messages = workspaceDiagnostics.Select(diagnostic => diagnostic.Message).ToArray();

		// Only read the project files when there is an unresolved reference to attribute, since status is asked often
		// and this is the only thing here that needs to know which projects name an SDK.
		var anyUnresolved = messages.Any(message => message.Contains(UnresolvedMarker, StringComparison.Ordinal));
		if (anyUnresolved && UnbuiltReference(build, messages, Outputs(solution), File.Exists) is { } unbuilt)
		{
			yield return unbuilt;
		}

		var stale = BuildFreshness.Of(solution, project: null, cancellationToken)
			.Where(project => project.Stale)
			.Select(project => project.Project)
			.ToArray();

		if (stale.Length == 0) yield break;

		yield return WorkspaceReason.Of(
			ReasonKinds.StaleBuildOutput,
			HealthEffect.Note,
			$"{stale.Length} project(s) have sources newer than their last build output, so anything run out "
				+ "of bin or obj is not this code. rose_build_freshness says which. This does not make the "
				+ "workspace degraded: these answers come from source.",
			stale);
	}

	/// <summary>
	/// Whether the platform this server chose looks like the wrong one, asked of the properties that
	/// chose it and answered from what MSBuild then complained about.
	/// </summary>
	private static string? WrongPlatformSuspicion(
		BuildProperties? build,
		IReadOnlyList<WorkspaceDiagnostic> diagnostics) =>
		build?.SuspectWrongPlatform(diagnostics.Select(diagnostic => diagnostic.Message), File.Exists);

	/// <summary>A project's identity and the file its build writes, which is what an unresolved reference names.</summary>
	/// <param name="Name">The project's name.</param>
	/// <param name="FilePath">The project file.</param>
	/// <param name="OutputFilePath">The assembly or metadata file its build writes; empty where Roslyn does not know it.</param>
	/// <param name="NamesSdk">
	/// Whether the project file names an SDK, which decides whether <c>dotnet build</c> can build it or it needs MSBuild.
	/// </param>
	public readonly record struct ProjectOutput(string Name, string FilePath, string OutputFilePath, bool NamesSdk = true);

	/// <summary>
	/// The notice for references the design-time build could not resolve because the in-solution project that
	/// writes them has not been built, naming each such project, what wanted it, and the build that fixes it;
	/// null when no load diagnostic is that.
	/// <para>
	/// MSBuild says only "Cannot resolve Assembly or Windows Metadata file", with a path. That is accurate and
	/// says nothing about what to do, and on a fresh clone or worktree it is the first thing status shows for
	/// a WinUI or UWP solution, whose design-time build looks for a referenced project's output on disk rather
	/// than in the solution. The path is the referenced project's own output, so the remedy is known exactly:
	/// build that project, then reload. A path no project in the solution writes is some other failure -- a
	/// package or SDK reference -- and is left to the raw diagnostic, since naming a project to build for it
	/// would be wrong advice.
	/// </para>
	/// <para>
	/// A notice rather than a degraded reason: the projects still load, and the remedy is a build rather than
	/// anything about whether to trust the answers. Where every path named has been built since the load, it
	/// says that a reload is all that is left.
	/// </para>
	/// <para>
	/// Nothing where the platform this server chose is suspected, which takes the same outputs existing under
	/// another declared platform: then the remedy is to reload under that one, and building under this one
	/// would build for the platform that is the mistake. Where nothing exists under any platform, as on a
	/// fresh clone, the platform is not suspected and the remedy is this one.
	/// </para>
	/// <para>
	/// The build named is the one the load ran under -- its configuration, platform and pinned properties,
	/// passed as the restore passes them -- because a plain build writes to a different output folder and the
	/// reference would stay unresolved after the caller did as told. A project that names no SDK, which is
	/// every UWP one, is built with MSBuild, since <c>dotnet build</c> cannot build it.
	/// </para>
	/// </summary>
	/// <param name="build">The properties the solution loaded under, which say whether the platform is suspect.</param>
	/// <param name="diagnosticMessages">The load diagnostics, as MSBuild worded them.</param>
	/// <param name="projects">Every project in the solution, with the file its build writes.</param>
	/// <param name="exists">Whether a file is on disk now.</param>
	public static string? UnbuiltReferenceNotice(
		BuildProperties? build,
		IReadOnlyList<string> diagnosticMessages,
		IReadOnlyList<ProjectOutput> projects,
		Func<string, bool> exists) =>
		UnbuiltReference(build, diagnosticMessages, projects, exists)?.Text;

	/// <summary>
	/// The notice <see cref="UnbuiltReferenceNotice"/> words, as a note covering the projects that have not been
	/// built, which are what a person accepting it would be accepting.
	/// </summary>
	private static WorkspaceReason? UnbuiltReference(
		BuildProperties? build,
		IReadOnlyList<string> diagnosticMessages,
		IReadOnlyList<ProjectOutput> projects,
		Func<string, bool> exists)
	{
		if (build?.SuspectWrongPlatform(diagnosticMessages, exists) is not null) return null;

		var unbuilt = new Dictionary<string, (ProjectOutput Producer, SortedSet<string> Wanting, List<string> Paths)>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var message in diagnosticMessages)
		{
			var unresolved = UnresolvedReference().Match(message);
			if (!unresolved.Success) continue;

			var path = unresolved.Groups["path"].Value;
			if (Producer(path, projects) is not { } producer) continue;

			if (!unbuilt.TryGetValue(producer.FilePath, out var entry))
			{
				entry = (producer, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), []);
				unbuilt[producer.FilePath] = entry;
			}

			entry.Paths.Add(path);

			var wanting = ProcessedProject().Match(message);
			if (wanting.Success) entry.Wanting.Add(Path.GetFileNameWithoutExtension(wanting.Groups["project"].Value));
		}

		if (unbuilt.Count == 0) return null;

		var named = string.Join(", ", unbuilt.Values.Select(entry => entry.Wanting.Count == 0
			? ProjectName(entry.Producer)
			: $"{ProjectName(entry.Producer)} (wanted by {string.Join(", ", entry.Wanting)})"));

		var producers = unbuilt.Values.Select(entry => ProjectName(entry.Producer)).ToArray();
		var one = unbuilt.Count == 1;
		var builtSince = unbuilt.Values.All(entry => entry.Paths.All(exists));
		if (builtSince)
		{
			return Unbuilt(
				$"The design-time build could not resolve the output of {named}, which had not been built when the "
					+ $"solution loaded. {(one ? "It has" : "They have")} been built since, so rose_workspace_reload clears this.");
		}

		var builds = string.Join(" and ", unbuilt.Values.Select(entry => BuildCommand(entry.Producer, build)));
		var needsMsBuild = unbuilt.Values.Any(entry => !entry.Producer.NamesSdk);
		var msBuild = needsMsBuild
			? " A project that names no SDK, which is every UWP one, is built with MSBuild, since dotnet build cannot build it."
			: string.Empty;

		return Unbuilt(
			$"The design-time build could not resolve the output of {named}, because {(one ? "it has" : "they have")} "
				+ "not been built: a WinUI or UWP project's design-time build looks for a referenced project's output on disk "
				+ $"rather than in the solution. Build {(one ? "it" : "them")} first, under the properties this load used, with "
				+ $"{builds}, then rose_workspace_reload.{msBuild}");

		WorkspaceReason Unbuilt(string text) =>
			WorkspaceReason.Of(ReasonKinds.UnbuiltReference, HealthEffect.Note, text, producers);
	}

	/// <summary>
	/// The command that builds a project under the properties the load ran under, so its output lands where the
	/// load looks: <c>dotnet build</c> for a project that names an SDK, <c>msbuild</c> for one that does not.
	/// </summary>
	private static string BuildCommand(ProjectOutput project, BuildProperties? build)
	{
		var tool = project.NamesSdk ? "dotnet build" : "msbuild";
		var properties = (build?.AsRestoreArguments() ?? [])
			.Select(argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument);

		return string.Join(" ", [tool, $"\"{project.FilePath}\"", .. properties]);
	}

	/// <summary>
	/// Every project with the file its build writes and whether it names an SDK. A project file that cannot be
	/// read is taken to name one, which is what every project this server can evaluate itself does.
	/// </summary>
	private static ProjectOutput[] Outputs(Solution solution) =>
	[
		.. solution.Projects.Select(project =>
			new ProjectOutput(
				project.Name,
				project.FilePath ?? string.Empty,
				project.OutputFilePath ?? string.Empty,
				NamesSdk(project.FilePath))),
	];

	private static bool NamesSdk(string? projectFile)
	{
		if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile)) return true;

		try
		{
			return ProjectItemStyle.NamesSdk(File.ReadAllText(projectFile));
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}

	/// <summary>The words MSBuild opens an unresolved reference with, which is what <see cref="UnresolvedReference"/> matches.</summary>
	private const string UnresolvedMarker = "Cannot resolve Assembly or Windows Metadata file";

	/// <summary>
	/// The project whose build writes a file, matched by the whole path where it can be and otherwise by file
	/// name, since a design-time build under other global properties can look for the same assembly under a
	/// different output folder.
	/// </summary>
	private static ProjectOutput? Producer(string path, IReadOnlyList<ProjectOutput> projects)
	{
		var writers = projects.Where(project => project.OutputFilePath.Length > 0).ToArray();

		foreach (var project in writers)
		{
			if (SamePath(project.OutputFilePath, path)) return project;
		}

		var fileName = Path.GetFileName(path);

		foreach (var project in writers)
		{
			if (Path.GetFileName(project.OutputFilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return project;
		}

		return null;
	}

	private static bool SamePath(string one, string other) =>
		string.Equals(
			one.Replace('/', '\\'),
			other.Replace('/', '\\'),
			StringComparison.OrdinalIgnoreCase);

	/// <summary>A project as a caller names it: its file's name, so the targets of a multi-targeted project read as one.</summary>
	private static string ProjectName(ProjectOutput project) =>
		project.FilePath.Length > 0 ? Path.GetFileNameWithoutExtension(project.FilePath) : project.Name;

	[GeneratedRegex(@"Cannot resolve Assembly or Windows Metadata file '(?<path>[^']+)'", RegexOptions.CultureInvariant)]
	private static partial Regex UnresolvedReference();

	[GeneratedRegex(@"processing the file '(?<project>[^']+)'", RegexOptions.CultureInvariant)]
	private static partial Regex ProcessedProject();

	/// <summary>
	/// The NuGet error codes a failed restore's output names, each once: what a failed restore is about, so a
	/// restore accepted as failing on an unreachable feed is not taken to cover one that fails on a missing
	/// package. Empty where the output names none, and the reason is then about its own wording.
	/// </summary>
	public static IEnumerable<string> RestoreErrorCodes(string? output) =>
		string.IsNullOrEmpty(output)
			? []
			: NuGetErrorCode().Matches(output).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase);

	[GeneratedRegex(@"\bNU\d{4}\b", RegexOptions.CultureInvariant)]
	private static partial Regex NuGetErrorCode();

	private static IReadOnlyList<WorkspaceReason> CollectDegradedReasons(
		IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
		IReadOnlyList<ProjectStatus> projects,
		RestoreReport? restore,
		BuildProperties? build,
		IReadOnlyList<AnalyzerLoadFailure> analyzerLoadFailures,
		IReadOnlyList<ProjectEvaluationFailure> evaluationFailures)
	{
		var reasons = new List<WorkspaceReason>();

		if (restore is { Ran: true, Succeeded: false })
		{
			reasons.Add(WorkspaceReason.Of(
				ReasonKinds.RestoreFailed,
				HealthEffect.BlocksAnswers,
				"Restore failed, so the design-time build could not resolve references or analyzers. "
					+ "Run dotnet restore and inspect the output.",
				subjects: RestoreErrorCodes(restore.Output)));
		}
		else if (UnrestoredReason(restore) is { } unrestored)
		{
			reasons.Add(WorkspaceReason.Of(
				ReasonKinds.Unrestored,
				HealthEffect.MayAffectSomeProjects,
				unrestored,
				restore!.Unrestored.Select(Path.GetFileNameWithoutExtension).OfType<string>()));
		}

		if (WrongPlatformSuspicion(build, workspaceDiagnostics) is { } platform)
		{
			reasons.Add(WorkspaceReason.Of(ReasonKinds.WrongPlatform, HealthEffect.BlocksAnswers, platform, subjects: [build!.Platform ?? string.Empty]));
		}

		foreach (var project in projects.Where(project => !project.LoadedSuccessfully))
		{
			reasons.Add(WorkspaceReason.Of(
				ReasonKinds.ProjectNotLoaded,
				HealthEffect.MayAffectSomeProjects,
				$"Project {project.Name} did not load successfully; its semantic results are unreliable.",
				[project.Name]));
		}

		foreach (var project in projects.Where(project => project.MissingAnalyzerOutputs.Count > 0))
		{
			var analyzerProjects = string.IsNullOrEmpty(project.FilePath)
				? new Dictionary<string, string>()
				: AnalyzerProjectReferences.ReadAnalyzerProjects(project.FilePath);

			foreach (var missing in project.MissingAnalyzerOutputs)
			{
				var remedy = analyzerProjects.TryGetValue(missing, out var owner)
					? $"Build it with: dotnet build {owner}"
					: "Restore or build the solution so the analyzer assembly exists, then reload.";

				reasons.Add(WorkspaceReason.Of(
					ReasonKinds.AnalyzerOutputMissing,
					HealthEffect.MayAffectSomeProjects,
					$"Project {project.Name} expects analyzer assembly {missing}, but the file is not on "
						+ "disk. MSBuild still passes it to the compiler, so any source generators it contains are "
						+ $"silently producing nothing. {remedy}",
					[project.Name]));
			}
		}

		// The file being there and not loading, which the missing-output check above cannot see. The
		// compiler is handed the reference either way, so the generators inside it produce nothing while
		// the project reports a clean load and a generator count of zero -- a healthy-looking workspace
		// that is not one, and the second of the three failures this server exists to prevent. Which projects
		// reference an assembly is not known once it has failed, so the reason covers the solution.
		if (AnalyzerReason(analyzerLoadFailures) is { } analyzers)
		{
			reasons.Add(WorkspaceReason.Of(
				ReasonKinds.AnalyzerLoadFailed,
				HealthEffect.MayAffectSomeProjects,
				analyzers,
				subjects: analyzerLoadFailures.Select(failure => failure.Assembly)));
		}

		// The design-time build runs in Roslyn's build host, a process of its own, so it can load every project
		// while this process's MSBuild evaluates none of them -- a workspace that reports Loaded from a worker
		// that has lost its SDK. Counted against distinct project files, which is what the evaluation walked:
		// a multi-targeted project is several projects here and one evaluation there.
		var projectFiles = projects
			.Select(project => project.FilePath)
			.Where(path => path.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Count();

		var loadedProjects = projects
			.Where(project => project.LoadedSuccessfully && project.FilePath.Length > 0)
			.Select(project => Path.GetFullPath(project.FilePath))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (EvaluationReason(evaluationFailures, projectFiles, loadedProjects) is { } evaluation)
		{
			// The worker being broken is a fact about every answer that resolves through MSBuild, so it covers the
			// solution; projects failing in their own right are a fact about those projects.
			reasons.Add(BlamesTheWorker(evaluationFailures, projectFiles, loadedProjects)
				? WorkspaceReason.Of(
				ReasonKinds.WorkerMsBuildBroken,
				HealthEffect.BlocksAnswers,
				evaluation,
				subjects: evaluationFailures.Where(failure => failure.NamesSdk).Select(failure => failure.Message))
				: WorkspaceReason.Of(
					ReasonKinds.ProjectNotEvaluated,
					HealthEffect.MayAffectSomeProjects,
					evaluation,
					evaluationFailures.Where(failure => failure.NamesSdk).Select(ProjectName)));
		}

		// Counted, but only degrading when something actually came back impaired. MSBuild's Failure
		// kind covers complaints that have no bearing on whether a project compiled, and a status
		// that reads Degraded on every solution on the machine tells a caller nothing it can act on.
		// It covers the impaired projects, so a project newly impaired is not covered by an acceptance
		// of the count as it stood.
		var failures = workspaceDiagnostics.Count(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure);
		var impaired = projects.Where(project => !project.LoadedSuccessfully).Select(project => project.Name).ToArray();

		if (failures > 0 && impaired.Length > 0)
		{
			reasons.Add(WorkspaceReason.Of(
				ReasonKinds.MsBuildLoadFailures,
				HealthEffect.MayAffectSomeProjects,
				$"MSBuild reported {failures} load failure(s); see loadDiagnostics.",
				impaired));
		}

		return reasons;
	}
}
