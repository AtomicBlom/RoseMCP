using RoseMcp.Contracts;
using RoseMcp.Worker.Xaml;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// Folding the degraded reasons: one per kind, never one per instance.
/// <para>
/// Measured need, on Drawboard's Drawboard.Pdf.slnx: 17 reasons, of which 9 were analyzer assemblies
/// that would not load and 8 were projects with unresolvable XAML types. The tray draws the whole
/// list into one information bar, so that is seventeen near-identical paragraphs deciding the height
/// of the card, and a remedy sentence repeated nine times.
/// </para>
/// </summary>
public sealed class DegradedReasonFoldingTests
{
	/// <summary>
	/// The finding these exist to surface: one generator arriving at several versions, all but one of
	/// them losing the single load context. The versions are what differ, so the assembly name has to
	/// carry a count or the collision reads as four unrelated broken packages.
	/// </summary>
	[Test]
	public void Analyzer_reason_groups_repeated_assemblies_under_one_count()
	{
		var reason = AnalyzerReason(
		[
			Failure("System.Text.Json.SourceGeneration.dll", "8.0.14.32403"),
			Failure("System.Text.Json.SourceGeneration.dll", "10.0.14.32716"),
			Failure("Microsoft.Extensions.Logging.Generators.dll", "8.0.9.3103"),
		]);

		reason.ShouldNotBeNull();
		reason.ShouldStartWith("3 analyzer assemblies failed to load", Case.Sensitive);
		reason.ShouldContain("System.Text.Json.SourceGeneration.dll (x2)", Case.Sensitive);

		// One occurrence is named without a count: "(x1)" would be noise on the common case.
		reason.ShouldContain("Microsoft.Extensions.Logging.Generators.dll", Case.Sensitive);
		reason.ShouldNotContain("(x1)", Case.Sensitive);
	}

	/// <summary>Nine failures are one reason, not nine. This is the whole point of the fold.</summary>
	[Test]
	public void Analyzer_reason_is_one_line_however_many_failed()
	{
		var many = Enumerable.Range(0, 9).Select(index => Failure($"Gen{index}.dll", "1.0.0.0")).ToArray();

		var reason = AnalyzerReason(many);

		reason.ShouldNotBeNull();
		reason.ShouldNotContain(Environment.NewLine, Case.Sensitive);

		// Named enough to act on, capped so the line cannot grow back into the list it replaced.
		reason.ShouldContain("and 6 more", Case.Sensitive);
	}

	[Test]
	public void Analyzer_reason_is_absent_when_every_assembly_loaded() =>
		AnalyzerReason([]).ShouldBeNull();

	/// <summary>
	/// The case that prompted all this: restore exits 0 having quietly skipped the projects it does
	/// not understand, so success is a fact about the command rather than about the solution.
	/// </summary>
	[Test]
	public void Unrestored_reason_contradicts_a_restore_that_reported_success()
	{
		var reason = UnrestoredReason(new RestoreReport
		{
			Ran = true,
			Reason = "62 of 105 project(s) had missing or stale restore output.",
			Succeeded = true,
			Unrestored = ["Db.App.csproj", "Db.Shared.Controls.csproj", "Db.Controls.csproj", "Db.Diagnostics.csproj"],
		});

		reason.ShouldNotBeNull();
		reason.ShouldStartWith("Restore reported success, but 4 projects have no restore output", Case.Sensitive);
		reason.ShouldContain("Db.App.csproj", Case.Sensitive);
		reason.ShouldContain("and 1 more", Case.Sensitive);
	}

	/// <summary>--no-restore asks to skip the work, not to stop reporting what state that leaves.</summary>
	[Test]
	public void Unrestored_reason_covers_a_restore_that_was_skipped()
	{
		var reason = UnrestoredReason(new RestoreReport
		{
			Ran = false,
			Reason = "Skipped: --no-restore was passed.",
			Unrestored = ["Db.App.csproj"],
		});

		reason.ShouldNotBeNull();
		reason.ShouldStartWith("Restore did not run, and 1 project has no restore output", Case.Sensitive);
	}

	[Test]
	public void Unrestored_reason_is_absent_when_every_project_restored() =>
		UnrestoredReason(new RestoreReport { Ran = true, Reason = "Ran.", Succeeded = true }).ShouldBeNull();

	[Test]
	public void Unrestored_reason_is_absent_when_restore_was_never_reported() =>
		UnrestoredReason(null).ShouldBeNull();

	/// <summary>
	/// Eight projects with unresolvable types are one reason naming eight projects. The types
	/// themselves are deliberately not in it -- ProjectStatus.UnresolvedXamlTypes already carries
	/// every one of them, and a reason that repeated them would be a second copy of the report.
	/// </summary>
	[Test]
	public void Xaml_reason_folds_unresolved_types_across_projects()
	{
		var reason = XamlReasons(
		[
			new XamlProjectReport("Db.App", Xaml(unresolved: ["AdvancedSettings: Expander", "PagesBox: NumberBox"])),
			new XamlProjectReport("Db.Shared.Controls", Xaml(unresolved: ["Icon: DbFontIcon"])),
		]).ShouldHaveSingleItem();

		reason.ShouldStartWith("3 named XAML elements across 2 projects", Case.Sensitive);
		reason.ShouldContain("Db.App (2)", Case.Sensitive);
		reason.ShouldContain("Db.Shared.Controls (1)", Case.Sensitive);

		// Points at the field that holds the detail rather than inlining it.
		reason.ShouldContain("unresolvedXamlTypes", Case.Sensitive);
	}

	/// <summary>
	/// One reason per kind, so a solution with two different XAML problems gets two lines -- the fold
	/// collapses repetition, and must not collapse distinct findings along with it.
	/// </summary>
	[Test]
	public void Xaml_reasons_stay_separate_per_kind()
	{
		var reasons = XamlReasons(
		[
			new XamlProjectReport("Db.App", Xaml(unresolved: ["PagesBox: NumberBox"])),
			new XamlProjectReport("Db.Legacy", Xaml(dialect: null, markupFiles: 4)),
		]).ToArray();

		reasons.Length.ShouldBe(2);
		reasons.ShouldContain(line => line.Contains("no dialect", StringComparison.Ordinal));
		reasons.ShouldContain(line => line.Contains("will not bind", StringComparison.Ordinal));
	}

	/// <summary>A solution whose XAML stubbed cleanly says nothing at all, and stays Loaded.</summary>
	[Test]
	public void Xaml_reasons_are_empty_when_every_stub_resolved() =>
		XamlReasons([new XamlProjectReport("Db.App", Xaml())]).ShouldBeEmpty();

	private static AnalyzerLoadFailure Failure(string assembly, string version) => new()
	{
		Assembly = assembly,
		Message = $"Could not load file or assembly '{Path.GetFileNameWithoutExtension(assembly)}, "
			+ $"Version={version}'. The located assembly's manifest definition does not match the assembly reference.",
	};

	private static XamlStubReport Xaml(
		string? dialect = "UWP",
		int markupFiles = 1,
		IReadOnlyList<string>? unresolved = null) => new()
		{
			Dialect = dialect,
			DialectReason = dialect is null ? "no XAML framework was referenced" : "Windows.UI.Xaml was referenced",
			DialectAmbiguous = false,
			MarkupFileCount = markupFiles,
			StubbedClassCount = markupFiles,
			UnresolvedTypes = unresolved ?? [],
			Skipped = [],
		};
}
