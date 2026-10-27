using RoseMcp.Contracts;
using RoseMcp.LiveApp;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>The live-app host's command line, as the broker writes it.</summary>
public sealed class LiveAppOptionsTests
{
	/// <summary>A session puts the toolbar in its target unless told not to, because that is what a person attaching expects.</summary>
	[Test]
	public void A_session_loads_the_toolbar_unless_told_not_to()
	{
		var options = LiveAppOptions.Parse(["--attach", "4120", "--description", "probe"]);

		options.Target.Kind.ShouldBe(LiveAppTargetKind.AttachProcess);
		options.Target.ProcessId.ShouldBe(4120);
		options.AttachesUiTooling.ShouldBeTrue();
	}

	[Test]
	public void No_ui_tooling_leaves_the_first_load_to_the_first_xaml_request()
	{
		LiveAppOptions.Parse(["--attach", "4120", "--no-ui-tooling"]).AttachesUiTooling.ShouldBeFalse();
	}
}
