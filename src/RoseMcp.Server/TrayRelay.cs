using System.Text.Json;
using System.Threading.Channels;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.Server;

/// <summary>
/// Forwards this session's tool calls to a tray that is already running, instead of starting
/// workers of our own.
/// <para>
/// The tray hosts a broker and serves MCP over http, so delegating to it needs no new protocol:
/// this is an MCP client of an endpoint that already exists. What it buys is the pair of things
/// neither transport gives alone -- one warm worker per solution shared by every session, because
/// the tray owns them all, and a correct answer to "which workspace did you mean", because a stdio
/// process knows the directory its client started it in and an http broker never can.
/// </para>
/// <para>
/// The tools are not redeclared here. Listing is forwarded too, so the surface cannot drift from
/// the tray's.
/// </para>
/// </summary>
public sealed class TrayRelay : IAsyncDisposable
{
	private const string WorkspaceArgument = "workspace";

	/// <summary>
	/// How long to keep trying to reach the tray again before giving up on a call.
	/// <para>
	/// Deploying restarts the tray, so a session that has been open all day meets a dead endpoint
	/// routinely rather than exceptionally. Long enough to ride out the restart itself, short enough
	/// that a tray which is actually gone does not hang the call: the caller is told to try again,
	/// and by then it is usually back.
	/// </para>
	/// </summary>
	private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(10);

	private readonly Uri _endpoint;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _logger;
	private readonly string _workingDirectory;
	private readonly SemaphoreSlim _reconnecting = new(1, 1);

	private McpClient _tray;

	private TrayRelay(Uri endpoint, McpClient tray, string workingDirectory, ILoggerFactory loggerFactory)
	{
		_endpoint = endpoint;
		_tray = tray;
		_workingDirectory = workingDirectory;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<TrayRelay>();
	}

	/// <summary>
	/// Connects to a tray if one is listening, or returns null so the caller can run its own broker.
	/// <para>
	/// A short timeout on purpose: this runs before the session can do anything, and a tray that is
	/// slow to answer is indistinguishable from one that is not there. Being wrong here costs a
	/// private set of workers, not correctness.
	/// </para>
	/// </summary>
	public static async Task<TrayRelay?> TryConnectAsync(
		ServerOptions options,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		var logger = loggerFactory.CreateLogger<TrayRelay>();
		var endpoint = new Uri($"http://{options.Host}:{options.Port}/");

		if (!await IsListeningAsync(options, cancellationToken))
		{
			logger.LogInformation("No tray at {Endpoint}; this session will own its workers.", endpoint);
			return null;
		}

		try
		{
			var tray = await ConnectAsync(endpoint, loggerFactory, TimeSpan.FromSeconds(2), cancellationToken);

			logger.LogInformation("Relaying to the tray at {Endpoint}; its workers are shared.", endpoint);

			return new TrayRelay(endpoint, tray, Environment.CurrentDirectory, loggerFactory);
		}
		catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
		{
			logger.LogInformation(
				"No tray at {Endpoint} ({Reason}); this session will own its workers.", endpoint, exception.Message);

			return null;
		}
	}

	/// <summary>
	/// Whether a tray is listening, asked without building anything.
	/// <para>
	/// A plain request rather than an MCP handshake, so the decision costs one round trip and needs
	/// no logger. That matters more than it looks: a logger factory built before the host exists
	/// claims a log file of its own, which left every relayed session with two -- one holding the
	/// handshake and one holding everything after it.
	/// </para>
	/// </summary>
	public static async Task<bool> IsListeningAsync(ServerOptions options, CancellationToken cancellationToken)
	{
		try
		{
			using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
			using var answer = await probe.GetAsync(
				new Uri($"http://{options.Host}:{options.Port}/admin/workspaces"), cancellationToken);

			return answer.IsSuccessStatusCode;
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	public ValueTask<ListToolsResult> ListToolsAsync(CancellationToken cancellationToken) =>
		InvokeAsync(
			async (tray, token) => new ListToolsResult
			{
				Tools = [.. (await tray.ListToolsAsync(cancellationToken: token)).Select(tool => tool.ProtocolTool)],
			},
			cancellationToken);

	public async ValueTask<CallToolResult> CallToolAsync(
		CallToolRequestParams request,
		McpServer client,
		CancellationToken cancellationToken)
	{
		var arguments = WithWorkspace(request.Arguments);

		await using var progress = OrderedProgress.For(client, request.ProgressToken);

		return await InvokeAsync(
			(tray, token) => CancellableToolCall.InvokeAsync(tray, request.Name, arguments, progress, token),
			cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_reconnecting.Dispose();
		await _tray.DisposeAsync();
	}

	/// <summary>
	/// Runs one call against the tray, reconnecting and trying once more if the connection has died
	/// underneath us.
	/// <para>
	/// One retry, not a loop: a second transport failure means the tray is genuinely unreachable
	/// rather than merely restarted, and retrying past that turns a clear failure into a hang. The
	/// retried call finds a tray with nothing loaded, so it pays the solution load again, which is
	/// correct and is why the failure message below promises a slow call rather than a fast one.
	/// </para>
	/// </summary>
	private async ValueTask<T> InvokeAsync<T>(
		Func<McpClient, CancellationToken, Task<T>> call,
		CancellationToken cancellationToken)
	{
		var tray = _tray;

		using var cancelling = cancellationToken.Register(
			() => _logger.LogDebug("The client cancelled this call."));

		try
		{
			return await call(tray, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogDebug("The call ended because the client cancelled it.");
			throw;
		}
		catch (Exception exception) when (IsTransportFailure(exception) && !cancellationToken.IsCancellationRequested)
		{
			_logger.LogWarning(exception, "The tray at {Endpoint} stopped answering; reconnecting.", _endpoint);

			var reconnected = await ReconnectAsync(tray, cancellationToken);

			return await call(reconnected, cancellationToken);
		}
	}

	/// <summary>
	/// Replaces the dead connection, unless another call got there first. Concurrent calls all fail
	/// at once when the tray goes down, and each of them reconnecting would leave one session
	/// holding several sockets to a tray that has only just come back.
	/// </summary>
	private async Task<McpClient> ReconnectAsync(McpClient stale, CancellationToken cancellationToken)
	{
		await _reconnecting.WaitAsync(cancellationToken);

		try
		{
			if (!ReferenceEquals(_tray, stale)) return _tray;

			try
			{
				await stale.DisposeAsync();
			}
			catch (Exception exception)
			{
				_logger.LogDebug(exception, "Disposing the dead tray connection failed, which is expected.");
			}

			try
			{
				_tray = await ConnectAsync(_endpoint, _loggerFactory, ReconnectWindow, cancellationToken);
				_logger.LogInformation("Reconnected to the tray at {Endpoint}.", _endpoint);

				return _tray;
			}
			catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
			{
				throw new McpException(
					$"The RoseMCP tray at {_endpoint} is not answering, so this session has nowhere to run the "
						+ "call. It restarts during a deploy, so trying again in a moment usually works, and the "
						+ "call after a restart is slow because the solution loads again.",
					exception);
			}
		}
		finally
		{
			_reconnecting.Release();
		}
	}

	/// <summary>
	/// Opens a connection, retrying until <paramref name="within"/> elapses. A tray that is mid
	/// restart refuses the socket outright, so the first attempt failing says nothing about the next.
	/// </summary>
	private static async Task<McpClient> ConnectAsync(
		Uri endpoint,
		ILoggerFactory loggerFactory,
		TimeSpan within,
		CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + within;

		while (true)
		{
			try
			{
				using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				attempt.CancelAfter(TimeSpan.FromSeconds(2));

				var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint });

				return await McpClient.CreateAsync(
					transport, loggerFactory: loggerFactory, cancellationToken: attempt.Token);
			}
			catch when (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
			}
		}
	}

	/// <summary>
	/// The connection died, as opposed to the call failing. Mirrors what the broker treats as a dead
	/// worker, plus the http failures a socket to a stopped tray produces.
	/// </summary>
	private static bool IsTransportFailure(Exception exception) => exception
		is ClientTransportClosedException
		or HttpRequestException
		or IOException
		or ObjectDisposedException
		or InvalidOperationException { Source: "ModelContextProtocol.Core" };

	/// <summary>
	/// Fills in the workspace a call left out, from the directory this process was started in.
	/// <para>
	/// This is the whole reason the relay is worth having. The tray serves every repository on the
	/// machine, so with two solutions open it cannot guess which one a bare call means and has to
	/// refuse. A stdio process cannot be ambiguous: its client chose its working directory. Filling
	/// the argument in here means the tray is never asked the unanswerable question.
	/// </para>
	/// <para>
	/// Only when the directory actually resolves. A session started above a solution, or somewhere
	/// with none at all, is better served by the tray's own inference than by an argument naming a
	/// directory that means nothing.
	/// </para>
	/// <para>
	/// A directory holding several solutions is the one case that is not filled in and not passed on
	/// either: <see cref="AmbiguousSolutionException"/> travels out of here to the caller. Falling
	/// through would be worse than throwing, because the tray then infers from what it has open --
	/// and with exactly one workspace open it would answer from that one, however unrelated the
	/// repository it belongs to.
	/// </para>
	/// </summary>
	private IReadOnlyDictionary<string, object?> WithWorkspace(IDictionary<string, JsonElement>? arguments)
	{
		var supplied = arguments?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value) ?? [];

		var alreadyThere = arguments is not null
			&& arguments.TryGetValue(WorkspaceArgument, out var existing)
			&& existing.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
			&& !string.IsNullOrWhiteSpace(existing.ToString());

		if (alreadyThere) return supplied;

		SolutionChoice choice;
		try
		{
			choice = SolutionResolver.Choose(_workingDirectory);
		}
		catch (ArgumentException)
		{
			return supplied;
		}

		supplied[WorkspaceArgument] = choice.SolutionPath;

		if (choice.WasContested)
		{
			_logger.LogDebug(
				"Filled in workspace {Workspace} from the working directory, {Reason}, from: {Candidates}.",
				choice.SolutionPath,
				choice.Reason,
				string.Join(", ", choice.Candidates));
		}
		else
		{
			_logger.LogDebug(
				"Filled in workspace {Workspace} from the working directory.", choice.SolutionPath);
		}

		return supplied;
	}

	/// <summary>
	/// Relays progress in the order it arrived.
	/// <para>
	/// Reporting each notification as it lands without waiting for it lets them overtake each other,
	/// which was observed: a four-project status arrived 4/4, 2/4, 1/4. A percentage that only ever
	/// rises is the whole reason a bar is worth looking at, so notifications are queued and sent by
	/// one pump that awaits each in turn.
	/// </para>
	/// </summary>
	private sealed class OrderedProgress : IProgress<ProgressNotificationValue>, IAsyncDisposable
	{
		private readonly Channel<ProgressNotificationValue> _queue =
			Channel.CreateUnbounded<ProgressNotificationValue>(new UnboundedChannelOptions { SingleReader = true });

		private readonly Task _pump;

		private OrderedProgress(McpServer client, ProgressToken token) => _pump = PumpAsync(client, token);

		/// <summary>Null when the caller did not ask for progress, so nothing is queued or sent.</summary>
		public static OrderedProgress? For(McpServer client, ProgressToken? token) =>
			token is { } asked ? new OrderedProgress(client, asked) : null;

		public void Report(ProgressNotificationValue value) => _queue.Writer.TryWrite(value);

		public async ValueTask DisposeAsync()
		{
			_queue.Writer.TryComplete();
			await _pump;
		}

		private async Task PumpAsync(McpServer client, ProgressToken token)
		{
			await foreach (var value in _queue.Reader.ReadAllAsync())
			{
				try
				{
					await client.NotifyProgressAsync(token, value);
				}
				catch (Exception)
				{
					// A client that has stopped listening must not fail the call it asked for.
				}
			}
		}
	}
}
