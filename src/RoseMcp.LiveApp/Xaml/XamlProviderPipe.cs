using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The host end of the provider channel: a named pipe the injected provider connects back on.
/// <para>
/// D14 chose files in an ACL'd folder over a pipe because "a named pipe from an AppContainer needs a
/// capability-aware ACL and is finicky". The first clause is true and the conclusion does not follow,
/// because this codebase already writes that ACL -- it just points it at a folder. The two SIDs that
/// go on the directory go on the pipe instead, and the direction is the easy one: creating a pipe
/// from inside an AppContainer is the finicky case, connecting to one that already grants your SID
/// is routine. The host is the long-lived supervisor at medium IL, so host-creates / app-connects is
/// both the easy direction and the one the design wants (#50).
/// </para>
/// <para>
/// The loopback restriction has nothing to say about this. AppContainer blocks loopback *sockets*
/// without an exemption, which is why reaching the tray's HTTP port from inside the app is awkward;
/// pipes live in <c>\Device\NamedPipe</c> and are gated by the DACL. "UWP cannot do named pipes" is
/// a certification rule about submitted packages, and an injected diagnostics DLL is in nobody's
/// package.
/// </para>
/// </summary>
public sealed class XamlProviderPipe : IDisposable
{
	/// <summary>ALL APPLICATION PACKAGES, and ALL RESTRICTED APPLICATION PACKAGES.</summary>
	private static readonly string[] AppContainerSids = ["S-1-15-2-1", "S-1-15-2-2"];

	private readonly ILogger _logger;

	/// <summary>
	/// Every frame of a connection after its first: the reply to a request the host sent. Unbounded
	/// because one request is in flight at a time, so nothing accumulates here that a caller is not
	/// already waiting on.
	/// </summary>
	private readonly Channel<string> _replies = Channel.CreateUnbounded<string>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

	private readonly CancellationTokenSource _stopping = new();

	private NamedPipeServerStream? _server;
	private Task? _pump;

	/// <summary>
	/// The greeting of the provider holding the far end. Replaced with a fresh, uncompleted source
	/// when one goes, or a caller waiting for the next provider is handed the departed one's greeting
	/// the moment it asks and takes a dead tap for a live one.
	/// </summary>
	private volatile TaskCompletionSource<string> _greeting = NewGreeting();

	private volatile bool _connected;

	public XamlProviderPipe(ILogger logger)
	{
		_logger = logger;

		// The target pid is not in the name on purpose: one host serves one app, and a name carrying
		// only our own pid is one a recycled pid cannot collide with while we are alive to hold it.
		Name = $"rosemcp-xaml-{Environment.ProcessId}-{Guid.NewGuid():N}";
	}

	/// <summary>The pipe name, without the <c>\\.\pipe\</c> prefix. Handed to the provider verbatim.</summary>
	public string Name { get; }

	/// <summary>
	/// Whether a provider is holding the far end and has greeted the host, which is the whole of what
	/// makes the channel able to carry a request.
	/// <para>
	/// Not <c>NamedPipeServerStream.IsConnected</c>, which is the server's own state rather than the
	/// far end's and stays true after the provider's process has gone. Every decision the recovery
	/// turns on is asked of this one property, so answering it from something that cannot observe a
	/// departure is a session that never hangs up and never listens again -- reporting a dead tap as
	/// present while every request on it spends its bound and times out.
	/// </para>
	/// </summary>
	public bool Connected => _connected;

	/// <summary>
	/// Creates the pipe and starts listening. Separate from construction because a failure here is a
	/// fact about this machine that the caller reports rather than an exception it cannot act on.
	/// </summary>
	public string? Listen()
	{
		if (_server is not null) return null;

		try
		{
			var security = new PipeSecurity();

			// The host itself, or nothing can read what it created.
			security.AddAccessRule(new PipeAccessRule(
				WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

			// The same two SIDs the work folder grants, for the same reason: the provider runs in the
			// app's AppContainer and has no other identity to grant.
			foreach (var sid in AppContainerSids)
			{
				security.AddAccessRule(new PipeAccessRule(
					new SecurityIdentifier(sid),
					PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
					AccessControlType.Allow));
			}

			var server = NamedPipeServerStreamAcl.Create(
				Name,
				PipeDirection.InOut,
				maxNumberOfServerInstances: 1,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous,
				inBufferSize: 0,
				outBufferSize: 0,
				security);

			_server = server;
			_pump = Task.Run(() => PumpAsync(server, _stopping.Token));

			_logger.LogInformation("XAML provider pipe listening on {PipeName}.", Name);
			return null;
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not create the XAML provider pipe {PipeName}.", Name);
			return $"Could not create the provider pipe: {exception.Message}";
		}
	}

	/// <summary>
	/// Waits for a provider to connect and greet the host, up to <paramref name="timeout"/>. Returns
	/// the greeting, or null if none arrived -- which is the whole question this class exists to
	/// answer before any request is moved onto it.
	/// </summary>
	/// <remarks>
	/// Hanging up on a departed provider and listening for the next one happens where the departure is
	/// observed rather than here. A re-injected provider dials this name as soon as it loads, and a
	/// host that only re-listens when somebody asks is not listening then: the new tap finds nothing,
	/// which reads as a provider that failed to load rather than as a channel nobody reopened.
	/// </remarks>
	public string? WaitForProvider(TimeSpan timeout)
	{
		if (_server is null) return null;

		// Snapshotted, because the source is replaced when a provider goes and a wait that re-read the
		// field would be answered by whichever connection happened to be current at the end.
		var greeting = _greeting;
		if (!greeting.Task.Wait(timeout))
		{
			_logger.LogWarning("The XAML provider did not connect to {PipeName} within {Seconds}s.", Name, timeout.TotalSeconds);
			return null;
		}

		return greeting.Task.Result;
	}

	/// <summary>
	/// Sends a request and returns the reply, or null when the provider is not there, does not
	/// answer, or answers with an empty frame.
	/// <para>
	/// No generation number, and that is the point of a pipe. Every handshake through a folder is
	/// "does this file exist", so the host has to stamp a number on the request and have the provider
	/// echo it back to tell this answer from the last one. A reply read from the pipe the request went
	/// out on is <em>this</em> request's answer by construction.
	/// </para>
	/// <para>
	/// Every step is bounded, and a step that expires says which pipe and how long it waited. It has
	/// to be said as well as returned: a null here reaches the caller as one sentence about a request
	/// that went unanswered, and which of the four steps stopped is the difference between a provider
	/// that has gone and an app whose UI thread has.
	/// </para>
	/// </summary>
	public string? Request(string request, TimeSpan timeout)
	{
		var server = _server;
		if (server is null || !_connected) return null;

		try
		{
			// A reply left behind by a request that gave up would be handed to this one as its answer,
			// which is the single failure a length-prefixed channel cannot detect for itself: the frame
			// is well formed and answers a different question.
			while (_replies.Reader.TryRead(out var stale))
			{
				_logger.LogWarning(
					"Discarding a late XAML provider reply of {Length} chars on {PipeName} before asking for '{Request}'.",
					stale.Length,
					Name,
					request);
			}

			var payload = Encoding.UTF8.GetBytes(request);
			var header = new byte[4];
			header[0] = (byte)(payload.Length & 0xFF);
			header[1] = (byte)((payload.Length >> 8) & 0xFF);
			header[2] = (byte)((payload.Length >> 16) & 0xFF);
			header[3] = (byte)((payload.Length >> 24) & 0xFF);

			var writing = server.WriteAsync(header, 0, 4);
			if (!writing.Wait(timeout)) return TimedOut(request, timeout, "sending the length");

			writing = server.WriteAsync(payload, 0, payload.Length);
			if (!writing.Wait(timeout)) return TimedOut(request, timeout, "sending the request");

			var flushing = server.FlushAsync();
			if (!flushing.Wait(timeout)) return TimedOut(request, timeout, "flushing the request");

			var reply = TakeReply(timeout);
			if (reply is null) return TimedOut(request, timeout, "waiting for the reply");

			return reply.Length == 0 ? null : reply;
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "The XAML provider request '{Request}' failed on {PipeName}.", request, Name);
			return null;
		}
	}

	/// <summary>
	/// The next frame the pump has read, or null if none arrives inside the bound. The pump owns every
	/// read on the stream, so a request takes its answer from here rather than from the pipe: one
	/// reader is what lets a departure be noticed between requests as well as during one.
	/// </summary>
	private string? TakeReply(TimeSpan timeout)
	{
		using var expiry = new CancellationTokenSource(timeout);

		try
		{
			return _replies.Reader.ReadAsync(expiry.Token).AsTask().GetAwaiter().GetResult();
		}
		catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
		{
			return null;
		}
	}

	/// <summary>
	/// Says which pipe stopped answering, at which step, and how long it was given, then returns null
	/// so the caller reports a request that went unanswered. The log line is the only place the step
	/// is visible, and the step is what separates a provider that has gone from an app that has
	/// stopped serving its UI thread.
	/// </summary>
	private string? TimedOut(string request, TimeSpan timeout, string step)
	{
		_logger.LogWarning(
			"The XAML provider pipe {PipeName} timed out after {Seconds}s {Step} for '{Request}'.",
			Name,
			timeout.TotalSeconds,
			step,
			request);

		return null;
	}

	/// <summary>
	/// Accepts a provider, reads every frame it sends, and listens again the moment it goes.
	/// <para>
	/// The read is what observes the far end: it comes back zero-length, or it fails. Nothing else on
	/// this channel can say, which is why the read runs continuously rather than only inside a
	/// request -- a provider that dies between calls would otherwise be noticed by nobody, and the
	/// host would go on believing a dead tap was there.
	/// </para>
	/// <para>
	/// Hanging up here rather than at the next call is the half that makes the recovery work. A
	/// stream a departed client left behind refuses every connection until it is disconnected, and a
	/// re-injected provider dials as soon as it loads.
	/// </para>
	/// </summary>
	private async Task PumpAsync(NamedPipeServerStream server, CancellationToken stopping)
	{
		while (!stopping.IsCancellationRequested)
		{
			try
			{
				await server.WaitForConnectionAsync(stopping);
			}
			catch (Exception exception)
			{
				if (!stopping.IsCancellationRequested)
				{
					_logger.LogWarning(exception, "The XAML provider pipe {PipeName} stopped listening.", Name);
				}

				return;
			}

			// The first frame of a connection is the greeting, and it is the only one nothing asked
			// for. Answered to WaitForProvider rather than queued, or the next request reads it as its
			// own reply and every answer after that is one behind.
			var greeted = false;
			while (true)
			{
				var frame = await ReadFrameAsync(server, stopping);
				if (frame is null) break;

				if (greeted)
				{
					_replies.Writer.TryWrite(frame);
					continue;
				}

				greeted = true;

				// Before the source is completed, so a caller released by the greeting cannot look at
				// Connected and be told the provider that just greeted it is not there.
				_connected = true;
				_greeting.TrySetResult(frame);

				_logger.LogInformation("A XAML provider connected on {PipeName} and said: {Greeting}", Name, frame);
			}

			HangUp(server);
		}
	}

	/// <summary>
	/// Forgets the provider that has gone and puts the stream back to listening. What is left of its
	/// connection is discarded first: a reply nobody collected answers a question the next provider
	/// was never asked, and a completed greeting would tell the next caller a departed tap is up.
	/// </summary>
	private void HangUp(NamedPipeServerStream server)
	{
		_connected = false;
		_greeting = NewGreeting();

		while (_replies.Reader.TryRead(out _))
		{
		}

		try
		{
			if (server.IsConnected) server.Disconnect();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
		{
			// A stream the far end or our own teardown already took down. There is nothing left to
			// hang up on, and the next WaitForConnectionAsync reports whichever of the two it was.
		}

		_logger.LogInformation("The XAML provider on {PipeName} has gone; listening for another.", Name);
	}

	/// <summary>
	/// One length-prefixed UTF-8 message, or null when the provider has gone or sent something that is
	/// not a frame. Length-prefixed because an encoding decision per file costs twice -- a
	/// <c>wofstream</c> narrowing UTF-16 to ANSI so a tree parses as zero elements, and a command file
	/// needing UTF-8-without-BOM because the reader is narrow. One framing removes the category.
	/// </summary>
	private async Task<string?> ReadFrameAsync(NamedPipeServerStream server, CancellationToken stopping)
	{
		var header = new byte[4];
		if (!await ReadExactlyAsync(server, header, stopping)) return null;

		var length = BinaryPrimitivesLength(header);
		if (length is < 0 or > 64 * 1024 * 1024)
		{
			_logger.LogWarning("The XAML provider sent a frame of {Length} bytes, which is not a length.", length);
			return null;
		}

		var payload = new byte[length];
		return await ReadExactlyAsync(server, payload, stopping) ? Encoding.UTF8.GetString(payload) : null;
	}

	private static int BinaryPrimitivesLength(byte[] header) =>
		header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);

	/// <summary>
	/// Fills the buffer, or reports that there is nobody on the far end. A zero-length read is the
	/// provider closing its handle, and a failed one is its process going; both are the departure the
	/// server's own connection state cannot see.
	/// </summary>
	private static async Task<bool> ReadExactlyAsync(NamedPipeServerStream server, byte[] buffer, CancellationToken stopping)
	{
		var read = 0;
		while (read < buffer.Length)
		{
			int got;
			try
			{
				got = await server.ReadAsync(buffer.AsMemory(read), stopping);
			}
			catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
			{
				return false;
			}

			if (got <= 0) return false;

			read += got;
		}

		return true;
	}

	private static TaskCompletionSource<string> NewGreeting() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	public void Dispose()
	{
		_stopping.Cancel();

		try
		{
			// Before the pump is waited on: a read blocked with nobody writing is ended by the handle
			// closing, and waiting first would wait for the provider to say something it never will.
			_server?.Dispose();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			// A pipe whose far end went first. Nothing to reclaim that the handle close does not.
		}

		// Bounded, because the token is disposed next and a pump still inside a read would register on
		// one that has gone. A pump that outlives the bound holds nothing but a closed handle.
		_pump?.Wait(TimeSpan.FromSeconds(2));

		_server = null;
		_pump = null;
		_stopping.Dispose();
	}
}
