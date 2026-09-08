using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A real <c>RoseMcp.Server</c> child process, spoken to over stdio by hand.
/// <para>
/// By hand rather than through an <c>McpClient</c>, because what these tests are about is the
/// transport itself: closing stdin without disposing anything, and reading the exact string a
/// failing call produces. Disposing an <c>McpClient</c> kills the child's process tree, which
/// answers the lifetime question before it is asked, and the client turns an error result into an
/// exception whose message is not necessarily the one that came over the wire.
/// </para>
/// </summary>
public sealed class RoseServerProcess : IDisposable
{
	private readonly Process _process;
	private readonly Task<string> _draining;
	private int _nextId = 1;

	private RoseServerProcess(Process process)
	{
		_process = process;

		// Drained rather than ignored: the server logs to stderr, and a full pipe buffer stops the
		// process the test is waiting on.
		_draining = process.StandardError.ReadToEndAsync();
	}

	public int Id => _process.Id;

	public bool HasExited => _process.HasExited;

	/// <summary>
	/// The server executable from this repository's own build output, in the configuration the tests
	/// were built in.
	/// <para>
	/// Found by walking up to the solution file rather than by a relative path from the test binary,
	/// which is the same reasoning as the live-app host's resolver: the depth from a test's output
	/// folder to the repository root is a fact about the SDK's layout rather than about this
	/// repository.
	/// </para>
	/// </summary>
	public static string ExecutablePath()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) throw new InvalidOperationException("Could not find RoseMcp.slnx above the test binary.");

		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";

		var executable = Path.Combine(
			directory.FullName,
			"src",
			"RoseMcp.Server",
			"bin",
			configuration,
			"net10.0",
			OperatingSystem.IsWindows() ? "RoseMcp.Server.exe" : "RoseMcp.Server");

		if (!File.Exists(executable))
		{
			throw new FileNotFoundException($"The server has not been built at {executable}.", executable);
		}

		return executable;
	}

	/// <summary>Starts a server, with its streams held so a test can close stdin on its own terms.</summary>
	public static RoseServerProcess Start(params string[] arguments)
	{
		var start = new ProcessStartInfo(ExecutablePath())
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in arguments) start.ArgumentList.Add(argument);

		var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start RoseMcp.Server.");

		return new RoseServerProcess(process);
	}

	/// <summary>
	/// A loopback port nothing is listening on, taken by binding and releasing. The gap between
	/// releasing and the server binding is a race in principle; in a test run on a developer's
	/// machine nothing else is claiming ports in that window, and the alternative -- a fixed port --
	/// collides with the tray, which is genuinely there.
	/// </summary>
	public static int FreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();

		return port;
	}

	/// <summary>Handshakes, so the session is a real one before anything is asked of it.</summary>
	public async Task InitializeAsync(CancellationToken cancellationToken)
	{
		var id = await SendAsync(
			"""{"jsonrpc":"2.0","id":ID,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"rose-tests","version":"1"}}}""",
			cancellationToken);

		await ReadReplyAsync(id, cancellationToken);
		await SendRawAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", cancellationToken);
	}

	/// <summary>
	/// Calls one tool and hands back the raw reply. Raw because the thing under test is sometimes the
	/// error string itself, and every layer between here and the wire is one that could supply a
	/// message of its own.
	/// </summary>
	public async Task<JsonDocument> CallToolAsync(string tool, string argumentsJson, CancellationToken cancellationToken)
	{
		var id = await SendAsync(
			"""{"jsonrpc":"2.0","id":ID,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
				.Replace("TOOL", tool, StringComparison.Ordinal)
				.Replace("ARGS", argumentsJson, StringComparison.Ordinal),
			cancellationToken);

		return JsonDocument.Parse(await ReadReplyAsync(id, cancellationToken));
	}

	/// <summary>Closes stdin and nothing else, which is a client going away and no more than that.</summary>
	public void CloseStandardInput() => _process.StandardInput.Close();

	/// <summary>
	/// Whether the process ended inside <paramref name="within"/>. Returned rather than asserted, so
	/// the caller writes the sentence that says what it was waiting for.
	/// </summary>
	public async Task<bool> WaitForExitAsync(TimeSpan within, CancellationToken cancellationToken)
	{
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(within);

		try
		{
			await _process.WaitForExitAsync(bounded.Token);
			return true;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	/// <summary>The next line of stdout carrying <paramref name="id"/>, skipping notifications.</summary>
	/// <remarks>
	/// Skipping matters: the broker reports progress as it loads a solution, and those notifications
	/// arrive on the same stream. Reading one line and calling it the reply reads a progress report
	/// as an answer.
	/// </remarks>
	private async Task<string> ReadReplyAsync(int id, CancellationToken cancellationToken)
	{
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(TimeSpan.FromMinutes(5));

		while (true)
		{
			string? line;
			try
			{
				line = await _process.StandardOutput.ReadLineAsync(bounded.Token);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new InvalidOperationException($"The server did not answer request {id} within five minutes.");
			}

			if (line is null) throw new InvalidOperationException($"The server closed stdout before answering request {id}.");

			using var message = JsonDocument.Parse(line);
			if (message.RootElement.TryGetProperty("id", out var replied) && replied.GetInt32() == id) return line;
		}
	}

	private async Task<int> SendAsync(string template, CancellationToken cancellationToken)
	{
		var id = _nextId++;
		await SendRawAsync(template.Replace("ID", id.ToString(), StringComparison.Ordinal), cancellationToken);

		return id;
	}

	private async Task SendRawAsync(string frame, CancellationToken cancellationToken)
	{
		await _process.StandardInput.WriteLineAsync(frame.AsMemory(), cancellationToken);
		await _process.StandardInput.FlushAsync(cancellationToken);
	}

	public void Dispose()
	{
		try
		{
			if (!_process.HasExited) _process.Kill(entireProcessTree: true);
		}
		catch (Exception)
		{
			// Already gone between the look and the kill, which is the outcome this wanted anyway.
		}

		_process.Dispose();
		_ = _draining;
	}
}
