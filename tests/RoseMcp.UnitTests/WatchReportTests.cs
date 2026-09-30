
namespace RoseMcp.UnitTests;

/// <summary>Two drains taken either side of a wait for git, read as one.</summary>
public sealed class WatchReportTests
{
	/// <summary>
	/// The later drain answers for the present. Git has finished by then, so the first drain's "in flight"
	/// must not carry over, or the barrier would go on treating a settled tree as one being written.
	/// </summary>
	[Test]
	public void The_later_report_answers_whether_git_is_still_writing()
	{
		var first = new WatchReport { Signal = WatchSignal.GitOperationInFlight | WatchSignal.FileChanges };
		var later = new WatchReport { Signal = WatchSignal.None };

		var combined = first.Then(later);

		combined.HasFlag(WatchSignal.GitOperationInFlight).ShouldBeFalse();
		combined.HasFlag(WatchSignal.FileChanges).ShouldBeTrue("what was heard before the wait still happened");
	}

	/// <summary>
	/// What either drain saw happen is kept: a build file that appeared mid-checkout is in the second drain,
	/// one heard before the wait is in the first, and the same file heard in both is one file.
	/// </summary>
	[Test]
	public void What_either_report_saw_happen_is_kept()
	{
		var first = new WatchReport
		{
			BuildFilesAppeared = [@"C:\repo\Directory.Build.props"],
			BuildFilesChanged = [@"C:\repo\build\Common.props"],
		};

		var later = new WatchReport
		{
			Signal = WatchSignal.EventsLost,
			BuildFilesAppeared = [@"C:\repo\src\Directory.Build.props", @"C:\REPO\Directory.Build.props"],
		};

		var combined = first.Then(later);

		combined.BuildFilesAppeared.Count.ShouldBe(2);
		combined.BuildFilesChanged.ShouldHaveSingleItem();
		combined.HasFlag(WatchSignal.EventsLost).ShouldBeTrue();
	}
}
