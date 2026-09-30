using RoseMcp.XamlDiff;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a live edit has already sent to a running app, per file (#12). This is the state that makes
/// the edit-to-live loop possible without the caller holding a copy of every file it edits, and the
/// decisions it makes are all about what can honestly be claimed on a first apply.
/// </summary>
public sealed class XamlApplyBaselineTests
{
	private const string First = """<Grid><TextBlock x:Name="Caption" FontSize="24" /></Grid>""";
	private const string Second = """<Grid><TextBlock x:Name="Caption" FontSize="40" /></Grid>""";
	private const string Third = """<Grid><TextBlock x:Name="Caption" FontSize="52" /></Grid>""";

	/// <summary>
	/// The whole point: after the first call, the caller passes a file and nothing else, and each apply
	/// is diffed against what the last one sent rather than against the original.
	/// </summary>
	[Test]
	public void Diffs_each_apply_against_what_the_last_one_sent()
	{
		var baseline = new XamlApplyBaseline();

		var registration = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.UnchangedSinceTargetStarted);
		registration.OldXaml.ShouldBeNull();

		var second = baseline.Prepare(@"C:\app\MainPage.xaml", Second, XamlBaselineAge.ChangedSinceTargetStarted);
		second.OldXaml.ShouldBe(First);

		baseline.Advance(@"C:\app\MainPage.xaml", Second);

		// Against the second version, not the first. Getting this wrong would re-send every edit made
		// since the app started on every apply -- which for a property is merely wasteful and for an
		// added element is a second copy of it.
		var third = baseline.Prepare(@"C:\app\MainPage.xaml", Third, XamlBaselineAge.ChangedSinceTargetStarted);
		third.OldXaml.ShouldBe(Second);
	}

	/// <summary>
	/// A first apply cannot know what the running app was built from, so it records and says so rather
	/// than diffing the file against itself -- which would find nothing and report success, quietly
	/// skipping the caller's first edit.
	/// </summary>
	[Test]
	public void Records_a_first_apply_instead_of_diffing_a_file_against_itself()
	{
		var baseline = new XamlApplyBaseline();

		var plan = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.ChangedSinceTargetStarted);

		plan.OldXaml.ShouldBeNull();
		plan.Note.ShouldNotBeNull();
		plan.Note.ShouldContain("MainPage.xaml", Case.Sensitive);
		plan.Note.ShouldContain("oldXaml", Case.Sensitive);
		baseline.Knows(@"C:\app\MainPage.xaml").ShouldBeTrue();
	}

	/// <summary>
	/// The three things that can be known about the file's age each get their own reason, and none of
	/// them claims more than it has. An unreadable start time in particular must not be reported as
	/// "the file has changed", which is a statement about the file with nothing behind it.
	/// </summary>
	[Test]
	[Arguments(XamlBaselineAge.UnchangedSinceTargetStarted, "Nothing has edited")]
	[Arguments(XamlBaselineAge.ChangedSinceTargetStarted, "no longer on disk")]
	[Arguments(XamlBaselineAge.Unknown, "could not be read")]
	public void Says_what_it_knows_about_the_files_age_and_no_more(XamlBaselineAge age, string expected)
	{
		var plan = new XamlApplyBaseline().Prepare(@"C:\app\MainPage.xaml", First, age);

		plan.OldXaml.ShouldBeNull();
		plan.Note!.ShouldContain(expected, Case.Sensitive);
	}

	/// <summary>
	/// An unchanged file is said to be unchanged. Nothing to apply and a diff that found nothing both
	/// come out as zero edits, and they mean different things to whoever asked -- one is a caller who
	/// has not saved, the other a change this engine cannot express.
	/// </summary>
	[Test]
	public void Says_when_the_file_has_not_changed_since_the_last_apply()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\app\MainPage.xaml", First);

		var plan = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.ChangedSinceTargetStarted);

		plan.OldXaml.ShouldBe(First);
		plan.Note!.ShouldContain("unchanged since the last apply", Case.Sensitive);
	}

	/// <summary>
	/// One file, two spellings. A path arrives from a client and again from this process's own
	/// resolution, and a case difference between them is routine on Windows -- two baselines for one
	/// file would make every second apply a first one.
	/// </summary>
	[Test]
	public void Treats_one_file_spelled_two_ways_as_one_file()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\App\MainPage.xaml", First);

		var plan = baseline.Prepare(@"c:\app\mainpage.XAML", Second, XamlBaselineAge.ChangedSinceTargetStarted);

		plan.OldXaml.ShouldBe(First);
	}

	/// <summary>
	/// Baselines are per file. Two files edited in the same session must not share one, or an apply to
	/// the second would be diffed against the first and produce edits addressed at the wrong tree.
	/// </summary>
	[Test]
	public void Keeps_a_baseline_for_each_file()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\app\MainPage.xaml", First);

		baseline.Knows(@"C:\app\Settings.xaml").ShouldBeFalse("a file with no baseline is not known");

		var plan = baseline.Prepare(@"C:\app\Settings.xaml", Second, XamlBaselineAge.UnchangedSinceTargetStarted);

		plan.OldXaml.ShouldBeNull();
		plan.Note!.ShouldContain("Settings.xaml", Case.Sensitive);
		baseline.Prepare(@"C:\app\MainPage.xaml", Third, XamlBaselineAge.Unknown).OldXaml.ShouldBe(First);
	}
}
