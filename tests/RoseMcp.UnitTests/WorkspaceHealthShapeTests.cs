using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;
using RoseMcp.Worker.Xaml;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// Each reason in a workspace's health has a shape a person can accept and an agent can acknowledge: a
/// kind that is the same at every load whatever the wording, an effect, and the projects it covers. The
/// kind and the projects are what an acceptance is keyed on, so a reason given the wrong ones is either
/// silenced by an acceptance that was never about it or never covered by the one that was.
/// </summary>
public sealed class WorkspaceHealthShapeTests
{
	/// <summary>
	/// A project whose design-time build resolved nothing may affect answers about that project alone, and
	/// names it; a failed restore blocks every answer and names none, so accepting it covers the solution.
	/// </summary>
	[Test]
	public async Task A_described_workspace_gives_each_reason_its_kind_effect_and_projects(CancellationToken cancellationToken)
	{
		using var workspace = new AdhocWorkspace();
		var solution = workspace.CurrentSolution.AddProject("Core", "Core", LanguageNames.CSharp).Solution;

		var report = await DescribeAsync(
			solution,
			Path.Combine(Path.GetTempPath(), "Shop.slnx"),
			[new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Msbuild failed when processing the file 'Core.csproj'.")],
			new RestoreReport { Ran = true, Reason = "Ran.", Succeeded = false },
			[],
			revision: 3,
			loadSeconds: 1,
			cancellationToken);

		report.State.ShouldBe(WorkspaceState.Degraded);
		report.Health.Select(reason => (reason.Kind, reason.Effect, string.Join(",", reason.Projects))).ShouldBe(
		[
			(ReasonKinds.RestoreFailed, HealthEffect.BlocksAnswers, ""),
			(ReasonKinds.ProjectNotLoaded, HealthEffect.MayAffectSomeProjects, "Core"),
			(ReasonKinds.MsBuildLoadFailures, HealthEffect.MayAffectSomeProjects, "Core"),
		]);

		report.Health.Select(reason => reason.Id).ShouldBe(
			[ReasonKinds.RestoreFailed, $"{ReasonKinds.ProjectNotLoaded}:Core", $"{ReasonKinds.MsBuildLoadFailures}:Core"]);

		// The text in full is where it always was; health carries only the line a read would quote.
		report.DegradedReasons.Count.ShouldBe(3);
		report.Health[1].Line.ShouldBe("Project Core did not load successfully; its semantic results are unreliable.");
	}

	/// <summary>Projects left unrestored are covered by name, as a caller names a project, without the file extension.</summary>
	[Test]
	public async Task Unrestored_projects_are_covered_by_their_names(CancellationToken cancellationToken)
	{
		using var workspace = new AdhocWorkspace();

		var report = await DescribeAsync(
			workspace.CurrentSolution,
			Path.Combine(Path.GetTempPath(), "Shop.slnx"),
			[],
			new RestoreReport { Ran = true, Reason = "Ran.", Succeeded = true, Unrestored = ["Legacy.Tests.csproj", "Ink.Tests.csproj"] },
			[],
			revision: 1,
			loadSeconds: 1,
			cancellationToken);

		var reason = report.Health.ShouldHaveSingleItem();
		reason.Kind.ShouldBe(ReasonKinds.Unrestored);
		reason.Projects.ShouldBe(["Legacy.Tests", "Ink.Tests"]);
		reason.Id.ShouldBe($"{ReasonKinds.Unrestored}:Legacy.Tests+1");
	}

	/// <summary>Each XAML reason is a kind of its own, covering only the projects it names.</summary>
	[Test]
	public void Xaml_reasons_cover_the_projects_they_name()
	{
		var reasons = XamlHealth(
		[
			new XamlProjectReport("Db.App", Xaml(unresolved: ["PagesBox: NumberBox"])),
			new XamlProjectReport("Db.Legacy", Xaml(dialect: null, markupFiles: 4)),
			new XamlProjectReport("Db.Clean", Xaml()),
		]).ToArray();

		reasons.Select(reason => (reason.Kind, string.Join(",", reason.Projects))).ShouldBe(
		[
			(ReasonKinds.XamlNoDialect, "Db.Legacy"),
			(ReasonKinds.XamlUnresolvedTypes, "Db.App"),
		]);

		reasons.ShouldAllBe(reason => reason.Effect == HealthEffect.MayAffectSomeProjects);
	}

	/// <summary>
	/// Markup in no dialect a stub stands in for, which its framework's own generator compiles in the workspace, has no
	/// false errors to warn about, beside a project whose markup nothing compiles.
	/// </summary>
	[Test]
	public void Markup_its_own_generator_compiles_raises_no_dialect_reason()
	{
		var reasons = XamlHealth(
		[
			new XamlProjectReport("Game.Ui", Xaml(dialect: null, markupFiles: 3, compiledBy: "Views.SourceGen")),
			new XamlProjectReport("Db.Legacy", Xaml(dialect: null, markupFiles: 4)),
		]).ToArray();

		var reason = reasons.ShouldHaveSingleItem();
		reason.Kind.ShouldBe(ReasonKinds.XamlNoDialect);
		reason.Projects.ShouldBe(["Db.Legacy"]);
	}

	/// <summary>Two reasons of one kind and project are still told apart, since an id is what a caller sends back.</summary>
	[Test]
	public void Ids_are_unique_in_one_answer()
	{
		var health = HealthOf(
		[
			WorkspaceReason.Of(ReasonKinds.AnalyzerOutputMissing, HealthEffect.MayAffectSomeProjects, "Project Core expects A.", ["Core"]),
			WorkspaceReason.Of(ReasonKinds.AnalyzerOutputMissing, HealthEffect.MayAffectSomeProjects, "Project Core expects B.", ["Core"]),
		]);

		health.Select(reason => reason.Id).ShouldBe(
			[$"{ReasonKinds.AnalyzerOutputMissing}:Core", $"{ReasonKinds.AnalyzerOutputMissing}:Core#2"]);
	}

	/// <summary>
	/// A note is health too, and never makes the workspace degraded: it leads the notices, the degraded
	/// reasons stay empty, and a notice about something that happened is not health at all.
	/// </summary>
	[Test]
	public void A_note_is_health_without_degrading_the_workspace()
	{
		var report = WithReasons(
			Report(),
			[WorkspaceReason.Of(ReasonKinds.StaleBuildOutput, HealthEffect.Note, "2 project(s) have sources newer.", ["Core", "Web"])],
			["Absorbed 1 external file change(s)."]);

		report.State.ShouldBe(WorkspaceState.Loaded);
		report.DegradedReasons.ShouldBeEmpty();
		report.Notices.ShouldBe(["2 project(s) have sources newer.", "Absorbed 1 external file change(s)."]);
		report.Health.ShouldHaveSingleItem().Effect.ShouldBe(HealthEffect.Note);
	}

	/// <summary>What a report says about its reasons reads back as the reasons it was written from, which is how a read finds them.</summary>
	[Test]
	public void A_report_reads_back_as_the_reasons_it_was_written_from()
	{
		WorkspaceReason[] reasons =
		[
			WorkspaceReason.Of(ReasonKinds.ConfigurationChosen, HealthEffect.Note, "Debug was chosen."),
			WorkspaceReason.Of(ReasonKinds.ProjectNotLoaded, HealthEffect.MayAffectSomeProjects, "Project Core did not load.", ["Core"]),
			WorkspaceReason.Of(ReasonKinds.WrongPlatform, HealthEffect.BlocksAnswers, "The platform looks wrong."),
		];

		var report = WithReasons(Report(), reasons, ["Absorbed 1 external file change(s)."]);

		// Degrading first, then the notes, which is the order the report's lists give them in.
		ReasonsOf(report).Select(Shape).ShouldBe([Shape(reasons[1]), Shape(reasons[2]), Shape(reasons[0])]);

		static string Shape(WorkspaceReason reason) =>
			$"{reason.Kind} {reason.Effect} [{string.Join(",", reason.Projects)}] {reason.Text}";
	}

	/// <summary>A failed restore is about the NuGet errors it reported, each once, read from output as dotnet restore writes it.</summary>
	[Test]
	public void A_failed_restore_is_about_its_nuget_error_codes()
	{
		const string Output = """
			  Determining projects to restore...
			D:\repo\Broken\Broken.csproj : error NU1301: Unable to load the service index for source https://nowhere.invalid/v3/index.json.
			D:\repo\Broken\Broken.csproj : error NU1101: Unable to find package Missing.Package. No packages exist with this id in source(s): nowhere
			D:\repo\App\App.csproj : error NU1301: Unable to load the service index for source https://nowhere.invalid/v3/index.json.
			  Failed to restore D:\repo\Broken\Broken.csproj (in 1.2 sec).
			""";

		RestoreErrorCodes(Output).ShouldBe(["NU1301", "NU1101"]);
		RestoreErrorCodes(null).ShouldBeEmpty();
		RestoreErrorCodes("Restore failed for a reason NuGet did not number.").ShouldBeEmpty();
	}

	private static WorkspaceStatusReport Report() => new()
	{
		SolutionPath = Path.Combine(Path.GetTempPath(), "Shop.slnx"),
		State = WorkspaceState.Loaded,
		Revision = 1,
		Projects = [],
		LoadDiagnostics = [],
		DegradedReasons = [],
	};

	private static XamlStubReport Xaml(
		string? dialect = "WinUI",
		int markupFiles = 2,
		IReadOnlyList<string>? unresolved = null,
		string? compiledBy = null) => new()
		{
			Dialect = dialect,
			DialectReason = dialect is null ? "no XAML framework is referenced" : "references Microsoft.WindowsAppSDK",
			DialectAmbiguous = false,
			MarkupFileCount = markupFiles,
			StubbedClassCount = dialect is null ? 0 : markupFiles,
			UnresolvedTypes = unresolved ?? [],
			Skipped = [],
			CompiledInWorkspaceBy = compiledBy,
		};
}
