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
	[Fact]
	public void Diffs_each_apply_against_what_the_last_one_sent()
	{
		var baseline = new XamlApplyBaseline();

		var registration = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.UnchangedSinceTargetStarted);
		Assert.Null(registration.OldXaml);

		var second = baseline.Prepare(@"C:\app\MainPage.xaml", Second, XamlBaselineAge.ChangedSinceTargetStarted);
		Assert.Equal(First, second.OldXaml);

		baseline.Advance(@"C:\app\MainPage.xaml", Second);

		// Against the second version, not the first. Getting this wrong would re-send every edit made
		// since the app started on every apply -- which for a property is merely wasteful and for an
		// added element is a second copy of it.
		var third = baseline.Prepare(@"C:\app\MainPage.xaml", Third, XamlBaselineAge.ChangedSinceTargetStarted);
		Assert.Equal(Second, third.OldXaml);
	}

	/// <summary>
	/// A first apply cannot know what the running app was built from, so it records and says so rather
	/// than diffing the file against itself -- which would find nothing and report success, quietly
	/// skipping the caller's first edit.
	/// </summary>
	[Fact]
	public void Records_a_first_apply_instead_of_diffing_a_file_against_itself()
	{
		var baseline = new XamlApplyBaseline();

		var plan = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.ChangedSinceTargetStarted);

		Assert.Null(plan.OldXaml);
		Assert.NotNull(plan.Note);
		Assert.Contains("MainPage.xaml", plan.Note);
		Assert.Contains("oldXaml", plan.Note);
		Assert.True(baseline.Knows(@"C:\app\MainPage.xaml"));
	}

	/// <summary>
	/// The three things that can be known about the file's age each get their own reason, and none of
	/// them claims more than it has. An unreadable start time in particular must not be reported as
	/// "the file has changed", which is a statement about the file with nothing behind it.
	/// </summary>
	[Theory]
	[InlineData(XamlBaselineAge.UnchangedSinceTargetStarted, "Nothing has edited")]
	[InlineData(XamlBaselineAge.ChangedSinceTargetStarted, "no longer on disk")]
	[InlineData(XamlBaselineAge.Unknown, "could not be read")]
	public void Says_what_it_knows_about_the_files_age_and_no_more(XamlBaselineAge age, string expected)
	{
		var plan = new XamlApplyBaseline().Prepare(@"C:\app\MainPage.xaml", First, age);

		Assert.Null(plan.OldXaml);
		Assert.Contains(expected, plan.Note);
	}

	/// <summary>
	/// An unchanged file is said to be unchanged. Nothing to apply and a diff that found nothing both
	/// come out as zero edits, and they mean different things to whoever asked -- one is a caller who
	/// has not saved, the other a change this engine cannot express.
	/// </summary>
	[Fact]
	public void Says_when_the_file_has_not_changed_since_the_last_apply()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\app\MainPage.xaml", First);

		var plan = baseline.Prepare(@"C:\app\MainPage.xaml", First, XamlBaselineAge.ChangedSinceTargetStarted);

		Assert.Equal(First, plan.OldXaml);
		Assert.Contains("unchanged since the last apply", plan.Note);
	}

	/// <summary>
	/// One file, two spellings. A path arrives from a client and again from this process's own
	/// resolution, and a case difference between them is routine on Windows -- two baselines for one
	/// file would make every second apply a first one.
	/// </summary>
	[Fact]
	public void Treats_one_file_spelled_two_ways_as_one_file()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\App\MainPage.xaml", First);

		var plan = baseline.Prepare(@"c:\app\mainpage.XAML", Second, XamlBaselineAge.ChangedSinceTargetStarted);

		Assert.Equal(First, plan.OldXaml);
	}

	/// <summary>
	/// Baselines are per file. Two files edited in the same session must not share one, or an apply to
	/// the second would be diffed against the first and produce edits addressed at the wrong tree.
	/// </summary>
	[Fact]
	public void Keeps_a_baseline_for_each_file()
	{
		var baseline = new XamlApplyBaseline();
		baseline.Advance(@"C:\app\MainPage.xaml", First);

		Assert.False(baseline.Knows(@"C:\app\Settings.xaml"));

		var plan = baseline.Prepare(@"C:\app\Settings.xaml", Second, XamlBaselineAge.UnchangedSinceTargetStarted);

		Assert.Null(plan.OldXaml);
		Assert.Contains("Settings.xaml", plan.Note);
		Assert.Equal(First, baseline.Prepare(@"C:\app\MainPage.xaml", Third, XamlBaselineAge.Unknown).OldXaml);
	}
}
