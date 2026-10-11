using System.Globalization;
using System.Net;
using System.Text.Json;

using ModelContextProtocol.Client;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// The tray's broker, over http: the upstream a relay reconnects to after a deploy restarts it.
/// <para>
/// Every request names the relay, with an id minted once per relay and kept across reconnects, and
/// the relay protocol it speaks and its process, which is how a deploy tells its own relays from
/// any other watching the same port. The watch is a request the broker holds open for its whole life, so
/// a killed tray -- which is what a deploy does -- ends it the moment the process goes, and the
/// relay reconnects without waiting for a call, which is what lets an idle session hear that its
/// tools changed.
/// </para>
/// </summary>
public sealed class TrayUpstream : RelayUpstream
{
	/// <summary>How long a single connection attempt may take; a tray mid-restart refuses outright, so the next attempt is the useful one.</summary>
	private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(2);

	/// <summary>The pause between attempts.</summary>
	private static readonly TimeSpan AttemptPause = TimeSpan.FromMilliseconds(500);

	/// <summary>How long the hello may take, which is the broker reading two fields.</summary>
	private static readonly TimeSpan HelloBudget = TimeSpan.FromSeconds(2);

	private readonly Uri _endpoint;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _logger;
	private readonly Dictionary<string, string> _headers;

	/// <summary>
	/// For the hello and the watch. No client-wide timeout: the watch is meant to last as long as the
	/// tray does, and every other request bounds itself.
	/// </summary>
	private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

	private int _saidCannotWatch;

	private int _saidCannotIntroduce;

	/// <summary>The tray at <paramref name="endpoint"/>.</summary>
	public TrayUpstream(Uri endpoint, ILoggerFactory loggerFactory)
	{
		_endpoint = endpoint;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<TrayUpstream>();
		_headers = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[RelayProtocol.RelayHeader] = RelayId,
			[RelayProtocol.VersionHeader] = RelayProtocol.Version.ToString(CultureInfo.InvariantCulture),
			[RelayProtocol.ProcessHeader] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
		};
	}

	/// <summary>This relay's id, the same on every connection it opens.</summary>
	public string RelayId { get; } = RelayProtocol.NewRelayId();

	/// <inheritdoc/>
	public override string Name => $"The RoseMCP tray at {_endpoint}";

	/// <inheritdoc/>
	public override bool Reconnects => true;

	/// <summary>
	/// Opens a connection, retrying until <paramref name="within"/> elapses. A tray that is mid
	/// restart refuses the socket outright, so the first attempt failing says nothing about the next.
	/// </summary>
	public override async Task<McpClient> ConnectAsync(TimeSpan within, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + within;

		while (true)
		{
			try
			{
				using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				attempt.CancelAfter(AttemptBudget);

				var transport = new HttpClientTransport(
					new HttpClientTransportOptions { Endpoint = _endpoint, AdditionalHeaders = _headers },
					_loggerFactory);

				var token = attempt.Token;

				// A reconnect happens inside whichever call found the tray gone, and the connection it
				// opens serves every call after that one.
				return await Detached.Run(() => McpClient.CreateAsync(
					transport, loggerFactory: _loggerFactory, cancellationToken: token));
			}
			catch when (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(AttemptPause, cancellationToken);
			}
		}
	}

	/// <inheritdoc/>
	public override async Task<RelayHello?> HelloAsync(McpClient client, CancellationToken cancellationToken)
	{
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		bounded.CancelAfter(HelloBudget);

		try
		{
			using var request = Request(RelayProtocol.HelloRoute);
			using var answer = await _http.SendAsync(request, bounded.Token);

			if (!answer.IsSuccessStatusCode) return null;

			return await answer.Content.ReadFromJsonAsync<RelayHello>(ContractJson.Options, bounded.Token);
		}
		catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
		{
			_logger.LogDebug(exception, "The tray at {Endpoint} did not say which broker it is.", _endpoint);
			return null;
		}
	}

	/// <summary>
	/// Holds the watch open until it ends. A tray that refuses it is from before the relay protocol, or
	/// one speaking a protocol this relay does not: then the watch waits until cancelled, and a lost
	/// tray is found by the next call instead.
	/// </summary>
	public override async Task<WatchEnd> WatchAsync(McpClient client, string? instance, CancellationToken cancellationToken)
	{
		using var request = Request(RelayProtocol.WatchRoute);
		using var answer = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

		if (!answer.IsSuccessStatusCode)
		{
			if (Interlocked.Exchange(ref _saidCannotWatch, 1) == 0)
			{
				_logger.LogWarning(
					"The tray at {Endpoint} refused the relay's watch ({Status}), so a restart is noticed on the next call "
						+ "rather than at once.",
					_endpoint,
					(int)answer.StatusCode);
			}

			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return WatchEnd.Closed;
		}

		using var body = new StreamReader(await answer.Content.ReadAsStreamAsync(cancellationToken));

		// The first line says which broker holds the watch. One other than the relay's own means the
		// tray restarted between the relay connecting and the watch arriving, and the connection the
		// relay holds is to a broker that has gone -- though the endpoint it names still answers it.
		var first = await body.ReadLineAsync(cancellationToken);
		if (first is null) return WatchEnd.Closed;

		var hello = Hello(first);
		var landedElsewhere = instance is not null && hello is not null && !string.Equals(hello.Instance, instance, StringComparison.Ordinal);
		if (landedElsewhere) return WatchEnd.ReachedAnother(hello!.Instance);

		while (await body.ReadLineAsync(cancellationToken) is not null)
		{
			// Nothing else is written; the loop ends when the response does.
		}

		return WatchEnd.Closed;
	}

	/// <summary>
	/// Posts what the relay knows of its session. A broker that refuses it is from before the route, and is
	/// said once at information; any other refusal is logged with the broker's reason. Neither throws: the
	/// broker keeps the table for a person to read, and a session works the same whether or not it is in it.
	/// </summary>
	public override async Task IntroduceAsync(RelayClient client, CancellationToken cancellationToken)
	{
		using var request = Request(RelayProtocol.ClientRoute, HttpMethod.Post);
		request.Content = JsonContent.Create(client, options: ContractJson.Options);

		using var answer = await _http.SendAsync(request, cancellationToken);
		if (answer.IsSuccessStatusCode) return;

		var unknownRoute = answer.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
		if (unknownRoute)
		{
			if (Interlocked.Exchange(ref _saidCannotIntroduce, 1) == 0)
			{
				_logger.LogInformation(
					"The tray at {Endpoint} does not keep a table of its clients, so this session is not named in its window.", _endpoint);
			}

			return;
		}

		var reason = await answer.Content.ReadAsStringAsync(cancellationToken);
		_logger.LogDebug(
			"The tray at {Endpoint} refused this relay's introduction ({Status}): {Reason}",
			_endpoint,
			(int)answer.StatusCode,
			reason.Length > 500 ? reason[..500] : reason);
	}

	/// <inheritdoc/>
	public override ValueTask DisposeAsync()
	{
		_http.Dispose();
		return ValueTask.CompletedTask;
	}

	private HttpRequestMessage Request(string route, HttpMethod? method = null)
	{
		var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(_endpoint, route));
		foreach (var (name, value) in _headers) request.Headers.Add(name, value);

		return request;
	}

	private static RelayHello? Hello(string line)
	{
		try
		{
			return JsonSerializer.Deserialize<RelayHello>(line, ContractJson.Options);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
