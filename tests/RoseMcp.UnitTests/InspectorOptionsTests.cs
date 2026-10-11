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

		options.Host.ShouldBe("127.0.0.1");
		options.Port.ShouldBe(5077);
		options.Token.ShouldBeNull();
		options.SessionId.ShouldBeNull();
		options.BaseAddress.ToString().ShouldBe("http://127.0.0.1:5077/");
	}

	[Test]
	public void Everything_the_tray_passes_is_read()
	{
		var options = InspectorOptions.Parse(["--port", "6100", "--token", "abc123", "--session", "a1b2c3d4"]);

		options.Port.ShouldBe(6100);
		options.Token.ShouldBe("abc123");
		options.SessionId.ShouldBe("a1b2c3d4");
		options.BaseAddress.ToString().ShouldBe("http://127.0.0.1:6100/");
	}

	/// <summary>
	/// No token is a state the window explains, not a reason to exit: somebody who ran the exe by
	/// hand is better served by a window telling them how to get one.
	/// </summary>
	[Test]
	public void A_missing_token_is_not_an_error()
	{
		var options = InspectorOptions.Parse(["--port", "5077"]);

		options.Token.ShouldBeNull();
	}

	/// <summary>
	/// What one RoseMCP app is one of: the broker it reads. Every window -- the RoseMCP window, and an Inspector
	/// window for each debugged process -- lives in the one app, so a second launch against the same broker is
	/// handed to it whatever window it asks for, and a broker on another port is another app.
	/// </summary>
	[Test]
	public void The_instance_key_is_the_broker()
	{
		var rose = InspectorOptions.Parse(["--port", "5077"]);
		rose.InstanceKey.ShouldBe("RoseMcp.App:127.0.0.1:5077");

		InspectorOptions.Parse(["--port", "5077", "--session", "s1", "--target-pid", "4242"]).InstanceKey.ShouldBe(rose.InstanceKey);
		InspectorOptions.Parse(["--host", "127.0.0.1", "--port", "5079"]).InstanceKey.ShouldNotBe(rose.InstanceKey);
	}

	/// <summary>
	/// A launch naming a session, a process or <c>--inspect</c> opens an Inspector window; one naming none of
	/// them opens the RoseMCP window, which is what the tray's icon asks for.
	/// </summary>
	[Test]
	public void Only_a_launch_about_debugging_opens_an_inspector()
	{
		InspectorOptions.Parse([]).OpensInspector.ShouldBeFalse();
		InspectorOptions.Parse(["--session", "s1"]).OpensInspector.ShouldBeTrue();
		InspectorOptions.Parse(["--target-pid", "4242"]).OpensInspector.ShouldBeTrue();

		var debugAnApp = InspectorOptions.Parse(["--inspect"]);
		debugAnApp.Inspect.ShouldBeTrue();
		debugAnApp.OpensInspector.ShouldBeTrue();
		debugAnApp.SessionId.ShouldBeNull();
	}

	[Test]
	public void A_target_pid_that_is_not_a_number_is_refused()
	{
		Should.Throw<ArgumentException>(() => InspectorOptions.Parse(["--target-pid", "many"])).ShouldBeOfType<ArgumentException>();
		Should.Throw<ArgumentException>(() => InspectorOptions.Parse(["--target-pid"])).ShouldBeOfType<ArgumentException>();
	}

	[Test]
	[Arguments("--nonsense")]
	[Arguments("--port")]
	[Arguments("--token")]
	[Arguments("--session")]
	[Arguments("--host")]
	public void A_switch_that_cannot_be_honoured_is_refused(string argument)
	{
		Should.Throw<ArgumentException>(() => InspectorOptions.Parse([argument])).ShouldBeOfType<ArgumentException>();
	}

	[Test]
	public void A_port_that_is_not_a_number_is_refused()
	{
		Should.Throw<ArgumentException>(() => InspectorOptions.Parse(["--port", "http"])).ShouldBeOfType<ArgumentException>();
	}
}
