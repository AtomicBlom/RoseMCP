using System.IO.Pipes;

using RoseMcp.Tap.Protocol;

[assembly: RoseMcpTap(
	Name = "TestTap",
	Module = "RoseMcp.TestTap.dll",
	Endpoint = "rosemcp-testtap-{pid}",
	EntryType = "RoseMcp.TestTap.Tap",
	Protocol = TapGreeting.ProtocolVersion)]

namespace RoseMcp.TestTap;

/// <summary>
/// A tap serving one element and nothing else. It connects to the host pipe its startup data names,
/// greets with the key it was handed and declares only <c>tree</c> and <c>detach</c>, so a test sees both
/// a command it serves and the host refusing one it does not.
/// </summary>
public static class Tap
{
	private static NamedPipeClientStream? _pipe;

	/// <summary>Connects to the host and starts answering, as an endpoint calls it: <c>workDir|pipeName|key</c>.</summary>
	public static void Start(string startupData)
	{
		var parts = startupData.Split('|');

		var pipe = new NamedPipeClientStream(".", parts[1], PipeDirection.InOut, PipeOptions.Asynchronous);
		pipe.Connect(TimeSpan.FromSeconds(5));
		TapWire.WriteMessage(pipe, TapGreeting.Write(parts[2], TapCapabilities.Of(TapCommands.Tree, TapCommands.Detach)));

		_pipe = pipe;
		new Thread(() => Serve(pipe)) { IsBackground = true, Name = "rosemcp-testtap" }.Start();
	}

	/// <summary>Hangs up on the host.</summary>
	public static void Stop()
	{
		_pipe?.Dispose();
		_pipe = null;
	}

	private static void Serve(Stream pipe)
	{
		while (TapWire.ReadMessage(pipe) is { } message)
		{
			if (!TapWire.TryReadFrame(message, out var id, out var request)) continue;

			var reply = request switch
			{
				TapCommands.Tree => TapRows.Tree(1, 0, 0, "TestTap.Root", "Root", string.Empty, 0, 0, "#Root"),
				TapCommands.Detach => "released",
				_ => string.Empty,
			};

			TapWire.WriteMessage(pipe, TapWire.Frame(id, reply));
		}
	}
}
