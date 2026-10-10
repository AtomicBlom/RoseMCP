using System.Diagnostics;
using System.Threading.Channels;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// Forwards this session's tool calls to a broker that is already running -- the tray's, almost
/// always -- instead of starting workers of our own, and outlives that broker being replaced.
/// <para>
/// The tray hosts a broker and serves MCP over http, so delegating to it needs no new protocol:
/// this is an MCP client of an endpoint that already exists. What it buys is the pair of things
/// neither transport gives alone -- one warm worker per solution shared by every session, because
/// the tray owns them all, and a correct answer to "which workspace did you mean", because a stdio
/// process knows the directory its client started it in and an http broker never can.
/// </para>
/// <para>
/// That second half is contributed rather than decided here: the directory goes out with every call
/// and the broker ranks it against the arguments. See <see cref="CallToolAsync"/> for what deciding
/// it in this process cost.
/// </para>
/// <para>
/// The tools are not redeclared here. Listing is forwarded too, so the surface cannot drift from
/// the tray's.
/// </para>
/// <para>
/// A deploy restarts the broker and leaves this process running, since an installed relay sits apart
/// from what a deploy replaces. So the relay watches the broker, reconnects to the new one by itself,
/// and compares what the new one offers with what its client last saw: the same tool definitions and
/// instructions mean the client hears nothing at the protocol level, and different ones mean one
/// <c>notifications/tools/list_changed</c>, after which the client lists the tools again without
/// reconnecting. Either way the first result after a restart carries one line saying what happened.
/// See <c>docs/decisions/a-deploy-replaces-the-relay-only-when-its-protocol-changes.md</c>.
/// </para>
/// </summary>
public sealed class TrayRelay : IAsyncDisposable
{
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

	/// <summary>
	/// How long a session that has just started waits for a tray before owning its workers instead.
	/// Short on purpose: this runs before the session can do anything, and a tray that is slow to
	/// answer is indistinguishable from one that is not there. Being wrong costs a private set of
	/// workers, not correctness.
	/// </summary>
	private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(2);

	/// <summary>
	/// How long reading a broker's tools and instance may take. Bounded on its own rather than by the
	/// call that found the broker gone, because that call may be cancelled while the comparison is the
	/// only thing that would tell an idle client its tools changed.
	/// </summary>
	private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);

	/// <summary>The pause between the watch's reconnect attempts: a tray mid-deploy is down for as long as the copy takes.</summary>
	private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(2);

	/// <summary>How long a ping may take to say the broker is still there when its watch ended.</summary>
	private static readonly TimeSpan PingBudget = TimeSpan.FromSeconds(3);

	private static readonly IReadOnlyDictionary<string, object?> NoArguments = new Dictionary<string, object?>();

	private readonly RelayUpstream _upstream;
	private readonly ILogger _logger;
	private readonly string _workingDirectory;

	/// <summary>
	/// The name this relay gives the session it serves, minted once for the life of the process and sent on every
	/// call. The tray's http transport has no session of its own to tell relayed clients apart by, and without this
	/// every one of them is the same caller to it: one agent acknowledging a reason would quiet it for all of them.
	/// See <see cref="CallSession.Of"/>.
	/// </summary>
	private readonly string _session = CallSession.Mint();
	private readonly SemaphoreSlim _reconnecting = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Lock _servedGate = new();
	private readonly PendingNotice _notice = new();
	private readonly DebugSessionMemory _debugSessions = new();

	/// <summary>
	/// Which of the tray's tools may be re-sent after the connection dies, learned from the tool list
	/// this relay forwards anyway.
	/// </summary>
	private readonly RelayRetryPolicy _retries = new();

	private McpClient _tray;

	/// <summary>The broker instance the relay is connected to, where the broker said. Written only under the reconnect gate.</summary>
	private string? _instance;

	/// <summary>The broker's tools and instructions as last read.</summary>
	private ToolSurface _surface;

	/// <summary>
	/// What the client holds: the tools as it last listed them through here, and the instructions it
	/// was given when it connected or last told had changed. Guarded by <see cref="_servedGate"/>.
	/// </summary>
	private ToolSurface _served;

	/// <summary>The scope of the watch in progress, cancelled when its connection is replaced so the watch moves to the new one.</summary>
	private CancellationTokenSource? _watch;

	private Task _watching = Task.CompletedTask;

	private TrayRelay(
		RelayUpstream upstream,
		McpClient tray,
		string? instance,
		ToolSurface surface,
		string workingDirectory,
		ILoggerFactory loggerFactory)
	{
		_upstream = upstream;
		_tray = tray;
		_instance = instance;
		_surface = surface;
		_served = surface;
		_workingDirectory = workingDirectory;
		_logger = loggerFactory.CreateLogger<TrayRelay>();
		Instructions = surface.Instructions;
	}

	/// <summary>
	/// The broker's server instructions, which the relay gives its own client when it connects.
	/// Without them a relayed session would get none at all, since its client never sees the broker's
	/// handshake.
	/// </summary>
	public string? Instructions { get; }

	/// <summary>The collection the relay's server is given, whose change sends the client <c>notifications/tools/list_changed</c>.</summary>
	public ToolListSignal ToolsChanged { get; } = new();

	/// <summary>
	/// Completes when the broker is gone for good: a broker this relay started has exited, and the
	/// session ends with it. A tray never completes it, since a tray is reconnected to.
	/// </summary>
	public Task Ended => _ended.Task;

	/// <summary>
	/// Connects to a tray if one is listening, or returns null so the caller can run its own broker.
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

		var upstream = new TrayUpstream(endpoint, loggerFactory);

		try
		{
			var relay = await ConnectAsync(upstream, Environment.CurrentDirectory, loggerFactory, StartupWindow, cancellationToken);

			logger.LogInformation(
				"Relaying to the tray at {Endpoint} as relay {Relay}; its workers are shared.", endpoint, upstream.RelayId);

			return relay;
		}
		catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
		{
			await upstream.DisposeAsync();

			logger.LogInformation(
				"No tray at {Endpoint} ({Reason}); this session will own its workers.", endpoint, exception.Message);

			return null;
		}
	}

	/// <summary>
	/// Connects through <paramref name="upstream"/>, reads what the broker offers, and starts watching
	/// it. The relay owns the upstream once this returns and disposes it; where this throws, the
	/// caller still does.
	/// </summary>
	public static async Task<TrayRelay> ConnectAsync(
		RelayUpstream upstream,
		string workingDirectory,
		ILoggerFactory loggerFactory,
		TimeSpan within,
		CancellationToken cancellationToken)
	{
		var tray = await upstream.ConnectAsync(within, cancellationToken);

		try
		{
			using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			reading.CancelAfter(ReadBudget);

			var hello = await upstream.HelloAsync(tray, reading.Token);
			var tools = await ListAsync(tray, reading.Token);

			var relay = new TrayRelay(
				upstream, tray, hello?.Instance, ToolSurface.Of(tools, tray.ServerInstructions), workingDirectory, loggerFactory);

			relay._retries.Learn(tools);
			relay.SayIfAnotherBuild(tray);
			relay.SayIfAnotherProtocol(hello);

			// Not any call's work: it outlives every call, and reconnects on behalf of all of them.
			relay._watching = Detached.Run(relay.WatchAsync);

			return relay;
		}
		catch
		{
			await tray.DisposeAsync();
			throw;
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

	/// <summary>Forwards a listing, and keeps it as what the client now holds.</summary>
	public ValueTask<ListToolsResult> ListToolsAsync(CancellationToken cancellationToken) =>
		InvokeAsync(
			async (tray, token) =>
			{
				var tools = await ListAsync(tray, token);

				// The same list the client gets, read once on the way past. It decides whether a call may
				// be re-sent after the connection dies, and it is what the client now holds, which is what
				// a broker met after a restart is compared with.
				_retries.Learn(tools);

				lock (_servedGate)
				{
					_served = ToolSurface.Of(tools, _served.Instructions);
				}

				return new ListToolsResult { Tools = tools };
			},
			retryable: true,
			cancellationToken);

	/// <summary>
	/// Forwards one call, adding the directory this session was started in and the name it serves under, and
	/// changing nothing else.
	/// <para>
	/// A fact, not a conclusion, and that distinction is the whole of it. This used to resolve the
	/// working directory to a solution and write the answer into the <c>workspace</c> argument, which
	/// went wrong three ways at once. It looked for an argument by name, so
	/// <c>rose_workspace_open</c> -- alone in calling its own <c>path</c> -- had an explicitly named
	/// solution treated as an omission and was refused for an ambiguity it had already settled. It
	/// resolved before reading the arguments, so a call carrying a file path that containment would
	/// have decided was refused too. And it ran for every tool, so <c>rose_debug_launch_uwp</c>, which
	/// takes no workspace and needs no solution, failed with a solution-ambiguity error.
	/// </para>
	/// <para>
	/// Sending the directory instead leaves one ordering, in the broker, over the whole set of
	/// arguments. It cannot pre-empt what the caller said, cannot miss an argument it does not know
	/// the name of, and cannot refuse on behalf of a tool that never asked.
	/// </para>
	/// <para>
	/// The one thing added on the way back is the restart notice, on the first result after a
	/// reconnect. It is claimed only once a result is in hand, so a call that fails on its way here
	/// leaves the line for the next one.
	/// </para>
	/// </summary>
	public async ValueTask<CallToolResult> CallToolAsync(
		CallToolRequestParams request,
		McpServer client,
		CancellationToken cancellationToken)
	{
		var arguments = request.Arguments?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
			?? [];

		await using var progress = OrderedProgress.For(client, request.ProgressToken);

		var result = await InvokeAsync(
			(tray, token) => CancellableToolCall.InvokeAsync(
				tray, request.Name, arguments, progress, token, _workingDirectory, session: _session),
			_retries.MayRetry(request.Name),
			cancellationToken);

		if (DebugSessionMemory.Changes(request.Name)) await RememberDebugSessionsAsync(cancellationToken);

		return _notice.Claim() is { } notice ? IgnoredArguments.Noticed(result, [notice.Describe()]) : result;
	}

	/// <summary>
	/// Stops watching, then closes the connection and the upstream. A broker this relay started goes
	/// with its connection, and its workers with it.
	/// <para>
	/// The reconnect gate and the lifetime source are left undisposed on purpose: a call still unwinding
	/// as the host stops may release the one or read the other's token, and neither holds anything to
	/// free while no wait handle has been asked of it and no timer set on it.
	/// </para>
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _lifetime.CancelAsync();

		try
		{
			await _watching;
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "The watch ended with an error as the relay closed.");
		}

		await DisposeQuietlyAsync(_tray);
		await _upstream.DisposeAsync();
	}

	private static async Task<Tool[]> ListAsync(McpClient tray, CancellationToken cancellationToken) =>
		[.. (await tray.ListToolsAsync(cancellationToken: cancellationToken)).Select(tool => tool.ProtocolTool)];

	/// <summary>
	/// Logs where the broker is another build than this relay, naming both commits.
	/// <para>
	/// Said, not refused, and only in the log, at information: the relay forwards rather than answers,
	/// so every tool a session sees is the broker's whichever build the relay is. An installed relay is
	/// older than the broker after every deploy that left the relay protocol alone, which is the
	/// ordinary state. What the line buys is the explanation for a session whose relay was started from
	/// some other copy.
	/// </para>
	/// </summary>
	private void SayIfAnotherBuild(McpClient tray)
	{
		var mismatch = BuildIdentity.Mismatch(
			_upstream.Name,
			BuildIdentity.FromHandshake(tray.ServerInfo?.Version),
			BuildIdentity.Of(typeof(TrayRelay).Assembly));

		if (mismatch is not null) _logger.LogInformation("{Mismatch}", mismatch);
	}

	/// <summary>Warns where the broker speaks another relay protocol, which is the one difference a reconnect cannot mend.</summary>
	private void SayIfAnotherProtocol(RelayHello? hello)
	{
		if (hello is null || hello.RelayProtocol == RelayProtocol.Version) return;

		_logger.LogWarning(
			"{Upstream} speaks relay protocol {Theirs} and this relay {Ours}; reconnecting the MCP client starts the relay installed with it.",
			_upstream.Name,
			hello.RelayProtocol,
			RelayProtocol.Version);
	}

	/// <summary>
	/// Reads which debug sessions this session has open, after a call that may have started or ended
	/// one, so a restart can name the ones that ended. Best effort: a reading that fails leaves the
	/// last one standing and never fails the call it follows.
	/// </summary>
	private async Task RememberDebugSessionsAsync(CancellationToken cancellationToken)
	{
		var tray = _tray;
		if (!_surface.Declares(ToolNames.DebugList)) return;

		var reading = _debugSessions.Begin();

		try
		{
			var listing = await CancellableToolCall.InvokeAsync(
				tray, ToolNames.DebugList, NoArguments, progress: null, cancellationToken, _workingDirectory);

			_debugSessions.Record(reading, DebugSessionMemory.Read(listing));
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Could not read which debug sessions are open, so a restart notice may not name them.");
		}
	}

	/// <summary>
	/// Runs one call against the tray, reconnecting if the connection has died underneath us, and
	/// trying once more only where re-sending is safe.
	/// </summary>
	/// <param name="call">The work to do against the tray.</param>
	/// <param name="retryable">
	/// Whether re-issuing this call can do no harm. A transport failure does not say whether the far
	/// side ran the call: a restarted tray never did, and a broken socket to a living tray may have
	/// applied the rename already. So a write is reported rather than repeated, and the caller is told
	/// what it does not know.
	/// </param>
	/// <param name="cancellationToken">The caller's token, which abandons the call rather than retrying it.</param>
	private async ValueTask<T> InvokeAsync<T>(
		Func<McpClient, CancellationToken, Task<T>> call,
		bool retryable,
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
			_logger.LogWarning(exception, "{Upstream} stopped answering; reconnecting.", _upstream.Name);

			// Reconnected either way, so the next call does not pay for this one's failure.
			var reconnected = await ReconnectAsync(tray, ReconnectWindow, cancellationToken);

			// One retry, not a loop: a second transport failure means the tray is genuinely unreachable
			// rather than merely restarted, and retrying past that turns a clear failure into a hang. The
			// retried call finds a tray with nothing loaded, so it pays the solution load again, which is
			// correct and is why the failure message elsewhere promises a slow call rather than a fast one.
			if (retryable) return await call(reconnected, cancellationToken);

			throw new McpException(
				"The connection to the tray dropped part-way through this call, which is not one that can "
					+ "be re-sent: the tray may have applied it before the connection went. The connection is "
					+ "back, so read the workspace and call again if it did not.",
				exception);
		}
	}

	/// <summary>
	/// Replaces the dead connection, unless another call got there first. Concurrent calls all fail
	/// at once when the tray goes down, and each of them reconnecting would leave one session
	/// holding several sockets to a tray that has only just come back.
	/// <para>
	/// The watch and the calls share it, so whichever notices first reconnects and the rest find the
	/// new connection waiting. What the new broker offers is compared inside it, so one restart is one
	/// comparison and at most one <c>list_changed</c>, and a call retrying after it finds the restart
	/// notice already waiting for its result. <paramref name="within"/> counts the wait for the gate
	/// too, so a call queued behind a watch that is still failing to reach the tray gets one more
	/// attempt rather than a second window of its own.
	/// </para>
	/// </summary>
	private async Task<McpClient> ReconnectAsync(McpClient stale, TimeSpan within, CancellationToken cancellationToken)
	{
		var asked = Stopwatch.GetTimestamp();
		await _reconnecting.WaitAsync(cancellationToken);

		try
		{
			if (!ReferenceEquals(_tray, stale)) return _tray;

			await DisposeQuietlyAsync(stale);

			if (!_upstream.Reconnects)
			{
				_ended.TrySetResult();

				throw new McpException(
					$"{_upstream.Name} has stopped, and this session cannot start another while a deploy may be "
						+ "replacing its files. Reconnect the MCP client (/mcp) to start a session on the build now installed.");
			}

			var left = within - Stopwatch.GetElapsedTime(asked);

			try
			{
				_tray = await _upstream.ConnectAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken);
			}
			catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
			{
				throw new McpException(
					$"{_upstream.Name} is not answering, so this session has nowhere to run the call. It restarts "
						+ "during a deploy, so trying again in a moment usually works, and the call after a restart is "
						+ "slow because the solution loads again.",
					exception);
			}

			_logger.LogInformation("Reconnected to {Upstream}.", _upstream.Name);

			// A reconnect is usually a deploy, which is exactly when the tray becomes another build.
			SayIfAnotherBuild(_tray);

			// Not the call's work, though a call may have found the broker gone: what it reads serves
			// every call after this one.
			var fresh = _tray;
			await Detached.Run(() => AdoptAsync(fresh));

			CancelQuietly(Volatile.Read(ref _watch));

			return fresh;
		}
		finally
		{
			_reconnecting.Release();
		}
	}

	/// <summary>
	/// Reads what a newly connected broker offers and decides what the client is told: one
	/// <c>list_changed</c> where its tools or instructions differ from what the client holds, nothing
	/// where they match, and a line on the next result wherever anything happened at all.
	/// </summary>
	private async Task AdoptAsync(McpClient fresh)
	{
		using var reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		reading.CancelAfter(ReadBudget);

		var hello = await _upstream.HelloAsync(fresh, reading.Token);

		ToolSurface? surface = null;

		try
		{
			var tools = await ListAsync(fresh, reading.Token);

			_retries.Learn(tools);
			surface = ToolSurface.Of(tools, fresh.ServerInstructions);
		}
		catch (Exception exception) when (!_lifetime.IsCancellationRequested)
		{
			_logger.LogWarning(exception, "Could not read the tools of {Upstream}; the client is asked to list them again.", _upstream.Name);
		}

		ToolSurfaceChange? change;

		lock (_servedGate)
		{
			change = surface is null ? null : _served.ChangesTo(surface);

			// The client cannot be given new instructions short of reconnecting, so once it has been told
			// they changed they count as served; otherwise every later reconnect would say so again.
			if (surface is not null && change is { InstructionsChanged: true }) _served = _served.WithInstructions(surface.Instructions);
		}

		var restarted = hello is null || _instance is null || !string.Equals(hello.Instance, _instance, StringComparison.Ordinal);
		var ended = _debugSessions.Take();
		int? otherProtocol = hello is not null && hello.RelayProtocol != RelayProtocol.Version ? hello.RelayProtocol : null;
		var refresh = change is not { IsEmpty: true };

		var worthSaying = restarted || ended.Count > 0 || refresh || otherProtocol is not null;
		if (worthSaying)
		{
			_notice.Add(new RestartNotice
			{
				Build = BuildIdentity.FromHandshake(fresh.ServerInfo?.Version),
				Restarted = restarted,
				EndedSessions = ended,
				Tools = change,
				BrokerRelayProtocol = otherProtocol,
			});
		}

		_instance = hello?.Instance;
		if (surface is not null) _surface = surface;

		SayIfAnotherProtocol(hello);

		if (!refresh) return;

		_logger.LogInformation("{Upstream} offers other tools than the client holds; telling it to list them again.", _upstream.Name);

		try
		{
			ToolsChanged.Raise();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Could not tell the client its tool list changed.");
		}
	}

	/// <summary>
	/// Watches the broker for as long as the relay lives, and reconnects when it goes.
	/// <para>
	/// A watch ending is a reason to look, not proof: the broker is asked before anything is replaced,
	/// because reconnecting to a broker that is still there opens a new connection, and the debug
	/// sessions this session started belong to the old one. A broker that is really gone is reconnected
	/// to until it is back -- a tray mid-deploy is down for as long as the copy takes -- unless it was
	/// one this relay started, which ends the relay instead.
	/// </para>
	/// </summary>
	private async Task WatchAsync()
	{
		var token = _lifetime.Token;

		while (!token.IsCancellationRequested)
		{
			var watched = _tray;

			using (var scope = CancellationTokenSource.CreateLinkedTokenSource(token))
			{
				// A full fence, so the read of the connection below cannot be ordered ahead of this write and
				// miss a reconnect that cancelled the scope before it was published.
				Interlocked.Exchange(ref _watch, scope);
				if (!ReferenceEquals(watched, _tray)) continue;

				try
				{
					await _upstream.WatchAsync(watched, _instance, scope.Token);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					return;
				}
				catch (Exception exception)
				{
					_logger.LogDebug(exception, "The watch on {Upstream} ended.", _upstream.Name);
				}
			}

			if (token.IsCancellationRequested) return;
			if (!ReferenceEquals(watched, _tray)) continue;

			if (await AnswersAsync(watched, token))
			{
				await PauseAsync(token);
				continue;
			}

			if (!_upstream.Reconnects)
			{
				_logger.LogWarning("{Upstream} has stopped; this session ends with it.", _upstream.Name);
				_ended.TrySetResult();

				return;
			}

			_logger.LogWarning("{Upstream} has gone; reconnecting.", _upstream.Name);
			await ReconnectUntilBackAsync(watched, token);
		}
	}

	/// <summary>Reconnects until a broker answers or the relay ends, pausing between attempts.</summary>
	private async Task ReconnectUntilBackAsync(McpClient stale, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await ReconnectAsync(stale, ReconnectWindow, token);
				return;
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				_logger.LogDebug(exception, "{Upstream} is not back yet.", _upstream.Name);
			}

			if (!await PauseAsync(token)) return;
		}
	}

	/// <summary>
	/// Whether the broker behind <paramref name="client"/> still answers. Any answer counts: on the
	/// 2026-07-28 revision there is no <c>ping</c>, and a live broker refuses it with a protocol
	/// error, which proves it is there as well as a reply would. Only a failure to reach it at all
	/// says it has gone.
	/// </summary>
	private static async Task<bool> AnswersAsync(McpClient client, CancellationToken token)
	{
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
		bounded.CancelAfter(PingBudget);

		try
		{
			await client.PingAsync(cancellationToken: bounded.Token);
			return true;
		}
		catch (McpProtocolException)
		{
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Waits <see cref="RetryPause"/>, or returns false at once when the relay is ending.</summary>
	private static async Task<bool> PauseAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(RetryPause, token);
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
	}

	/// <summary>Cancels a watch's scope, which may already have been disposed by the watch finishing.</summary>
	private static void CancelQuietly(CancellationTokenSource? scope)
	{
		try
		{
			scope?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The watch it scoped has already ended, which is what cancelling it was for.
		}
	}

	private async Task DisposeQuietlyAsync(McpClient client)
	{
		try
		{
			await client.DisposeAsync();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Disposing the dead tray connection failed, which is expected.");
		}
	}

	/// <summary>
	/// The connection died, as opposed to the call failing. Mirrors what the broker treats as a dead
	/// worker, plus the http failures a socket to a stopped tray produces.
	/// <para>
	/// A cancellation counts, and it is the one that was missing. The caller's own cancellation is
	/// caught before this is ever consulted, so an <see cref="OperationCanceledException"/> arriving
	/// here was raised by the transport rather than asked for -- which is exactly what a request on a
	/// client whose session has been disposed throws, instantly and as a
	/// <c>TaskCanceledException</c> out of <c>HttpClient</c>.
	/// </para>
	/// <para>
	/// Leaving it out is what made the second failure after a tray restart worse than the first. The
	/// first call fails on the socket, reconnects, cannot, and reports why; the dead client stays in
	/// the field, so every call after it fails this way instead -- unrecognised, so no reconnect, no
	/// message, and a bare shrug for a caller who has just been told something useful once.
	/// </para>
	/// </summary>
	private static bool IsTransportFailure(Exception exception) => exception
		is ClientTransportClosedException
		or HttpRequestException
		or IOException
		or ObjectDisposedException
		or OperationCanceledException
		or InvalidOperationException { Source: "ModelContextProtocol.Core" };

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
