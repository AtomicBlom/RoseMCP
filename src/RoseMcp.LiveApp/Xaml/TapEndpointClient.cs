using System.IO.Pipes;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using RoseMcp.Contracts;
using RoseMcp.Tap.Protocol;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The host's side of a framework's diagnostics endpoint: connects to the pipe it listens on inside a
/// target, checks the process serving that pipe is the target, and asks it to load a dropped-in tap.
/// The managed counterpart of <c>InitializeXamlDiagnosticsEx</c>, and like it, it only gets a tap
/// resident: everything after is the same pipe and protocol the in-box taps speak.
/// <para>
/// Public so the Windows integration suite can drive it against an endpoint in its own process, which is
/// the only way to make an endpoint refuse, answer late or be served by the wrong process on demand.
/// </para>
/// </summary>
public static partial class TapEndpointClient
{
	/// <summary>
	/// Asks the endpoint of <paramref name="tap"/>'s framework in process <paramref name="pid"/> to load the
	/// tap and start it with <paramref name="startupData"/>. Returns null once the endpoint says the tap was
	/// started, or the sentence saying why it was not.
	/// <para>
	/// Every step is bounded by <paramref name="bound"/>, because the endpoint lives in somebody else's
	/// process and a step that waits without a bound is a tool call that never returns.
	/// </para>
	/// </summary>
	public static string? Load(int pid, DroppedInTap tap, string startupData, TimeSpan bound)
	{
		var pipeName = EndpointWire.PipeName(tap.Endpoint, pid);
		using var expiry = new CancellationTokenSource(bound);

		try
		{
			using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

			try
			{
				client.ConnectAsync(expiry.Token).GetAwaiter().GetResult();
			}
			catch (OperationCanceledException)
			{
				return XamlChannelBounds.TimedOut($"the {tap.Name} diagnostics endpoint on {pipeName}", bound)
					+ " The pipe exists, so the app is listening, but every instance stayed busy: another host may be "
					+ "loading a tap into it at the same moment.";
			}

			// Before anything is sent. The endpoint is named for the target's process id, and nothing stops
			// another process creating that name first; this host's pipe name and key go in the next message,
			// and whoever receives them can pose as the tap.
			var server = ServerProcessId(client.SafePipeHandle);
			if (server != pid)
			{
				return $"The pipe {pipeName} is served by {(server is { } other ? $"process {other}" : "a process that could not be identified")}, "
					+ $"not by the target, pid {pid}, so nothing was sent to it. Something other than the app created the "
					+ $"{tap.Name} endpoint's pipe.";
			}

			var greeting = TapWire.ReadMessageAsync(client, expiry.Token).GetAwaiter().GetResult();
			if (greeting is null)
			{
				return Unanswered(tap, pipeName, bound, expiry, "its greeting");
			}

			if (!EndpointWire.TryReadGreeting(greeting, out var version, out var framework, out var frameworkVersion) || version < 1)
			{
				return $"The {tap.Name} endpoint on {pipeName} greeted with '{Shortened(greeting)}', which is not an endpoint's greeting.";
			}

			TapWire.WriteMessageAsync(client, EndpointWire.LoadRequest(tap.AssemblyPath, tap.EntryType, startupData), expiry.Token)
				.GetAwaiter()
				.GetResult();

			var reply = TapWire.ReadMessageAsync(client, expiry.Token).GetAwaiter().GetResult();
			if (reply is null) return Unanswered(tap, pipeName, bound, expiry, "its answer to loading the tap");

			var (loaded, detail) = EndpointWire.ReadReply(reply);
			return loaded
				? null
				: $"The {framework} {frameworkVersion} endpoint in pid {pid} refused to load the {tap.Name} tap from "
					+ $"{tap.AssemblyPath}: {detail}";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			return $"Could not reach the {tap.Name} diagnostics endpoint on {pipeName}: {exception.Message}";
		}
	}

	/// <summary>
	/// Whether a pipe of this name exists on this machine. Read from the pipe namespace's listing rather than
	/// by opening the pipe, because opening it is a connection: the endpoint would accept it, greet nobody,
	/// and the host that really wants it would wait behind the ghost.
	/// </summary>
	public static bool Listening(string pipeName)
	{
		try
		{
			var wanted = @"\\.\pipe\" + pipeName;
			return Directory.EnumerateFiles(@"\\.\pipe\").Any(pipe => string.Equals(pipe, wanted, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>The process serving the far end of a connected pipe, or null where Windows cannot say.</summary>
	private static int? ServerProcessId(SafePipeHandle pipe) =>
		GetNamedPipeServerProcessId(pipe, out var processId) ? (int)processId : null;

	private static string Unanswered(DroppedInTap tap, string pipeName, TimeSpan bound, CancellationTokenSource expiry, string what) =>
		expiry.IsCancellationRequested
			? XamlChannelBounds.TimedOut($"the {tap.Name} diagnostics endpoint on {pipeName}, waiting for {what}", bound)
			: $"The {tap.Name} diagnostics endpoint on {pipeName} hung up before sending {what}.";

	private static string Shortened(string text) => text.Length <= 60 ? text : text[..60] + "...";

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
