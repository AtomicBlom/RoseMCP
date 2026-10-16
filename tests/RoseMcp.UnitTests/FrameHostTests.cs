using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// What an attach naming <c>ApplicationFrameHost</c> is made to: the frame host owns every packaged app's window, so
/// it is what a process found by its window title is, and it runs no .NET.
/// </summary>
public sealed class FrameHostTests
{
	/// <summary>Any other process is attached to as named, with nothing said about it.</summary>
	[Test]
	public void A_process_that_is_not_the_frame_host_is_left_as_named()
	{
		var (processId, notice) = FrameHost.Resolve(1234, framed: null);

		processId.ShouldBe(1234);
		notice.ShouldBeNull();
	}

	/// <summary>Framing one .NET app, it means that app, and the move is said, naming the app and why.</summary>
	[Test]
	public void The_frame_host_framing_one_app_is_that_app()
	{
		var (processId, notice) = FrameHost.Resolve(29552, [new FramedApp(33760, "Drawboard Projects")]);

		processId.ShouldBe(33760);
		notice.ShouldNotBeNull();
		notice.ShouldContain("ApplicationFrameHost", Case.Sensitive);
		notice.ShouldContain("'Drawboard Projects', pid 33760", Case.Sensitive);
	}

	/// <summary>Framing several, which one was meant is not known, so each is named for the caller to choose.</summary>
	[Test]
	public void The_frame_host_framing_several_apps_is_refused_naming_each()
	{
		var refused = Should.Throw<ArgumentException>(() => FrameHost.Resolve(
			29552,
			[new FramedApp(33760, "Drawboard Projects"), new FramedApp(52092, "WinUI 2 Gallery")]));

		refused.Message.ShouldContain("pid 33760, 'Drawboard Projects'", Case.Sensitive);
		refused.Message.ShouldContain("pid 52092, 'WinUI 2 Gallery'", Case.Sensitive);
		refused.Message.ShouldContain("own pid", Case.Sensitive);
	}

	/// <summary>Framing none, there is nothing to attach to, and the caller is told where the app is found instead.</summary>
	[Test]
	public void The_frame_host_framing_no_app_is_refused()
	{
		var refused = Should.Throw<ArgumentException>(() => FrameHost.Resolve(29552, []));

		refused.Message.ShouldContain("No .NET app has a window in it", Case.Sensitive);
		refused.Message.ShouldContain("rose_debug_launch_uwp", Case.Sensitive);
	}
}
