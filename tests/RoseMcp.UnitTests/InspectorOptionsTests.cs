using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The command line the tray builds for the inspector, read back.
/// <para>
/// Strict on purpose: a switch silently ignored leaves a window pointed at the wrong port with
/// nothing on screen to say why, and the reader's conclusion is that the tray is broken.
/// </para>
/// </summary>
public sealed class InspectorOptionsTests
{
	[Test]
	public void Defaults_point_at_the_tray_on_loopback()
	{
		var options = InspectorOptions.Parse([]);

		Assert.Equal("127.0.0.1", options.Host);
		Assert.Equal(5077, options.Port);
		Assert.Null(options.Token);
		Assert.Null(options.SessionId);
		Assert.Equal("http://127.0.0.1:5077/", options.BaseAddress.ToString());
	}

	[Test]
	public void Everything_the_tray_passes_is_read()
	{
		var options = InspectorOptions.Parse(["--port", "6100", "--token", "abc123", "--session", "a1b2c3d4"]);

		Assert.Equal(6100, options.Port);
		Assert.Equal("abc123", options.Token);
		Assert.Equal("a1b2c3d4", options.SessionId);
		Assert.Equal("http://127.0.0.1:6100/", options.BaseAddress.ToString());
	}

	/// <summary>
	/// No token is a state the window explains, not a reason to exit: somebody who ran the exe by
	/// hand is better served by a window telling them how to get one.
	/// </summary>
	[Test]
	public void A_missing_token_is_not_an_error()
	{
		var options = InspectorOptions.Parse(["--port", "5077"]);

		Assert.Null(options.Token);
	}

	/// <summary>
	/// What one inspector is one of. The process rather than the session, so detaching and
	/// attaching again reaches the window already open on that program -- and because the title
	/// names the process, two windows keyed on sessions would be two windows with one name.
	/// </summary>
	[Test]
	public void The_instance_key_is_the_process_being_debugged()
	{
		var byProcess = InspectorOptions.Parse(["--session", "s1", "--target-pid", "4242"]);
		Assert.Equal(4242, byProcess.TargetProcessId);
		Assert.Equal("RoseMcp.Inspector:pid:4242", byProcess.InstanceKey);

		// Two sessions over one process are one window, which is the whole point of keying on it.
		Assert.Equal(
			byProcess.InstanceKey,
			InspectorOptions.Parse(["--session", "s2", "--target-pid", "4242"]).InstanceKey);

		// Two processes are two windows.
		Assert.NotEqual(
			byProcess.InstanceKey,
			InspectorOptions.Parse(["--session", "s1", "--target-pid", "77"]).InstanceKey);
	}

	/// <summary>
	/// With no process named, the session is the next best identity, and with neither there is one
	/// inspector. Both are fallbacks for a launch that did not come from the tray.
	/// </summary>
	[Test]
	public void Without_a_process_the_key_falls_back()
	{
		Assert.Equal("RoseMcp.Inspector:session:s1", InspectorOptions.Parse(["--session", "s1"]).InstanceKey);
		Assert.Equal("RoseMcp.Inspector", InspectorOptions.Parse([]).InstanceKey);
	}

	[Test]
	public void A_target_pid_that_is_not_a_number_is_refused()
	{
		Assert.Throws<ArgumentException>(() => InspectorOptions.Parse(["--target-pid", "many"]));
		Assert.Throws<ArgumentException>(() => InspectorOptions.Parse(["--target-pid"]));
	}

	[Test]
	[Arguments("--nonsense")]
	[Arguments("--port")]
	[Arguments("--token")]
	[Arguments("--session")]
	[Arguments("--host")]
	public void A_switch_that_cannot_be_honoured_is_refused(string argument)
	{
		Assert.Throws<ArgumentException>(() => InspectorOptions.Parse([argument]));
	}

	[Test]
	public void A_port_that_is_not_a_number_is_refused()
	{
		Assert.Throws<ArgumentException>(() => InspectorOptions.Parse(["--port", "http"]));
	}
}
