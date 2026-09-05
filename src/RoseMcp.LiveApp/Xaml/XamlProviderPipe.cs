using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

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
	private NamedPipeServerStream? _server;

	public XamlProviderPipe(ILogger logger)
	{
		_logger = logger;

		// The target pid is not in the name on purpose: one host serves one app, and a name carrying
		// only our own pid is one a recycled pid cannot collide with while we are alive to hold it.
		Name = $"rosemcp-xaml-{Environment.ProcessId}-{Guid.NewGuid():N}";
	}

	/// <summary>The pipe name, without the <c>\\.\pipe\</c> prefix. Handed to the provider verbatim.</summary>
	public string Name { get; }

	/// <summary>Whether the provider has connected and is holding the far end.</summary>
	public bool Connected => _server?.IsConnected ?? false;

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

			_server = NamedPipeServerStreamAcl.Create(
				Name,
				PipeDirection.InOut,
				maxNumberOfServerInstances: 1,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous,
				inBufferSize: 0,
				outBufferSize: 0,
				security);

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
	/// Waits for the provider to connect, up to <paramref name="timeout"/>. Returns the greeting it
	/// sent, or null if it never arrived -- which is the whole question this class exists to answer
	/// before any request is moved onto it.
	/// </summary>
	public string? WaitForProvider(TimeSpan timeout)
	{
		if (_server is null) return null;

		try
		{
			if (!_server.IsConnected)
			{
				var waiting = _server.WaitForConnectionAsync();
				if (!waiting.Wait(timeout))
				{
					_logger.LogWarning("The XAML provider did not connect to {PipeName} within {Seconds}s.", Name, timeout.TotalSeconds);
					return null;
				}
			}

			return ReadFrame(timeout);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Waiting for the XAML provider on {PipeName} failed.", Name);
			return null;
		}
	}

	/// <summary>
	/// Sends a request and returns the reply, or null when the provider is not there, does not
	/// answer, or answers with an empty frame.
	/// <para>
	/// An empty reply means "not served on the pipe", which is what lets one verb move at a time: the
	/// caller falls back to the file channel rather than failing, so the branch is never broken
	/// halfway through the move.
	/// </para>
	/// <para>
	/// No generation number, and that is the point of a pipe. Every handshake through the folder was
	/// "does this file exist", so the host had to stamp a number on the request and have the provider
	/// echo it back to tell this answer from the last one (#57, #89). A reply read from the pipe the
	/// request went out on is *this* request's answer by construction.
	/// </para>
	/// </summary>
	public string? Request(string request, TimeSpan timeout)
	{
		if (_server is null || !_server.IsConnected) return null;

		try
		{
			var payload = Encoding.UTF8.GetBytes(request);
			var header = new byte[4];
			header[0] = (byte)(payload.Length & 0xFF);
			header[1] = (byte)((payload.Length >> 8) & 0xFF);
			header[2] = (byte)((payload.Length >> 16) & 0xFF);
			header[3] = (byte)((payload.Length >> 24) & 0xFF);

			var writing = _server.WriteAsync(header, 0, 4);
			if (!writing.Wait(timeout)) return null;

			writing = _server.WriteAsync(payload, 0, payload.Length);
			if (!writing.Wait(timeout)) return null;

			var flushing = _server.FlushAsync();
			if (!flushing.Wait(timeout)) return null;

			var reply = ReadFrame(timeout);
			return string.IsNullOrEmpty(reply) ? null : reply;
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "The XAML provider request '{Request}' failed on {PipeName}.", request, Name);
			return null;
		}
	}

	/// <summary>
	/// One length-prefixed UTF-8 message. Length-prefixed because the file channel made an encoding
	/// decision per file and paid for it twice -- a <c>wofstream</c> narrowing UTF-16 to ANSI so a
	/// tree parsed as zero elements, and a command file needing UTF-8-without-BOM because the reader
	/// was narrow. One framing removes the category.
	/// </summary>
	private string? ReadFrame(TimeSpan timeout)
	{
		if (_server is null) return null;

		var header = new byte[4];
		if (!ReadExactly(header, timeout)) return null;

		var length = BinaryPrimitivesLength(header);
		if (length is < 0 or > 64 * 1024 * 1024)
		{
			_logger.LogWarning("The XAML provider sent a frame of {Length} bytes, which is not a length.", length);
			return null;
		}

		var payload = new byte[length];
		return ReadExactly(payload, timeout) ? Encoding.UTF8.GetString(payload) : null;
	}

	private static int BinaryPrimitivesLength(byte[] header) =>
		header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);

	private bool ReadExactly(byte[] buffer, TimeSpan timeout)
	{
		if (_server is null) return false;

		var read = 0;
		while (read < buffer.Length)
		{
			var reading = _server.ReadAsync(buffer, read, buffer.Length - read);
			if (!reading.Wait(timeout)) return false;

			var got = reading.Result;
			if (got <= 0) return false;

			read += got;
		}

		return true;
	}

	public void Dispose()
	{
		try
		{
			_server?.Dispose();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			// A pipe whose far end went first. Nothing to reclaim that the handle close does not.
		}

		_server = null;
	}
}
