using System.Collections.Concurrent;
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
	private readonly ConcurrentQueue<string> _notifications = new();

	// Whether the local application data folder is this process's own to delete, rather than one a caller named.
	private readonly bool _ownsLocalAppData;

	private RoseServerProcess(Process process, string localAppData, bool ownsLocalAppData)
	{
		_process = process;
		LocalAppData = localAppData;
		_ownsLocalAppData = ownsLocalAppData;

		// Drained rather than ignored: the server logs to stderr, and a full pipe buffer stops the
		// process the test is waiting on.
		_draining = process.StandardError.ReadToEndAsync();
	}

	public int Id => _process.Id;

	/// <summary>
	/// Where the server keeps its per-machine records (<c>ROSEMCP_LOCAL_APP_DATA</c>): a folder of its own under the
	/// temp directory, gone when this is disposed, unless the caller named one. Never the user's own, which the
	/// installed tray reads, and which a suite run would otherwise fill with a test's accepted reasons and recent targets.
	/// </summary>
	public string LocalAppData { get; }

	public bool HasExited => _process.HasExited;

	/// <summary>The exit code, once <see cref="HasExited"/>.</summary>
	public int ExitCode => _process.ExitCode;

	/// <summary>Everything the process wrote to stderr, complete once it has exited.</summary>
	public Task<string> StandardError => _draining;

	/// <summary>The method of every notification read past on the way to a reply, in the order they came.</summary>
	public IReadOnlyCollection<string> Notifications => _notifications;

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

	/// <summary>
	/// Starts a server, with its streams held so a test can close stdin on its own terms. It is given
	/// a port of its own unless the caller names one.
	/// </summary>
	public static RoseServerProcess Start(params string[] arguments) => StartIn(workingDirectory: null, arguments);

	/// <summary>
	/// Starts a server in a given directory. That directory is not decoration: it is the one fact a
	/// stdio session has that an http broker cannot learn, and the relay sends it with every call.
	/// </summary>
	public static RoseServerProcess StartIn(string? workingDirectory, params string[] arguments) =>
		StartExecutable(ExecutablePath(), workingDirectory, arguments);

	/// <summary>
	/// Starts a server from somewhere other than this repository's build output -- a copy staged where
	/// an install would put it, for the behaviour that depends on where the executable sits.
	/// </summary>
	public static RoseServerProcess StartExecutable(string executable, string? workingDirectory, params string[] arguments)
	{
		var start = new ProcessStartInfo(executable)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;

		foreach (var argument in WithAPortOfItsOwn(arguments)) start.ArgumentList.Add(argument);

		return Launch(start, environment: null);
	}

	/// <summary>
	/// Starts a server with environment variables of its own.
	/// <para>
	/// For the one setting a test has to know rather than discover: with no <c>ROSEMCP_TOKEN</c> an
	/// http server mints its operator token and says so only in its log, so a test that wants to make
	/// an authorised request has to choose the token itself.
	/// </para>
	/// </summary>
	public static RoseServerProcess StartWith(
		IReadOnlyDictionary<string, string> environment,
		params string[] arguments)
	{
		var start = new ProcessStartInfo(ExecutablePath())
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in WithAPortOfItsOwn(arguments)) start.ArgumentList.Add(argument);
		return Launch(start, environment);
	}

	/// <summary>The variable the server reads its local application data folder from.</summary>
	private const string LocalAppDataVariable = "ROSEMCP_LOCAL_APP_DATA";

	/// <summary>
	/// Starts the server <paramref name="start"/> describes, with <paramref name="environment"/> on top of a local
	/// application data folder of its own, which an entry of the caller's for the same variable replaces.
	/// </summary>
	private static RoseServerProcess Launch(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment)
	{
		var own = Path.Combine(Path.GetTempPath(), "RoseMcp.IntegrationTests", "Server", Guid.NewGuid().ToString("N"));
		start.Environment[LocalAppDataVariable] = own;

		foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;

		var localAppData = start.Environment[LocalAppDataVariable] ?? own;
		var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start RoseMcp.Server.");

		return new RoseServerProcess(process, localAppData, ownsLocalAppData: localAppData == own);
	}

	/// <summary>
	/// The arguments, with a port of this process's choosing added when the caller has not named one.
	/// <para>
	/// Not a convenience. A stdio server given no <c>--port</c> probes 5077 before it builds anything,
	/// and relays to whatever answers there instead of starting workers of its own. On a machine that
	/// is running the tray -- which is most of the time, since that is how this repository is
	/// dogfooded -- a test that omitted the argument would quietly run against the tray's workers and
	/// the solutions it already has open, proving nothing about the build under test. A port nothing
	/// answers on makes that probe fail, which is what every test here wants, so the suite takes one
	/// rather than leaving it to each call site to remember.
	/// </para>
	/// </summary>
	private static IEnumerable<string> WithAPortOfItsOwn(string[] arguments) =>
		arguments.Contains("--port") ? arguments : [.. arguments, "--port", FreePort().ToString()];

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
	/// as an answer. Each one skipped is kept in <see cref="Notifications"/>, since whether one was
	/// sent at all is sometimes the thing under test.
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

			var isNotification = !message.RootElement.TryGetProperty("id", out _)
				&& message.RootElement.TryGetProperty("method", out _);

			if (isNotification) _notifications.Enqueue(message.RootElement.GetProperty("method").GetString()!);
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

		try
		{
			if (_ownsLocalAppData && Directory.Exists(LocalAppData)) Directory.Delete(LocalAppData, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A child the kill has not yet let go of the folder; one left under the temp directory is litter, not a failure.
		}
	}
}
