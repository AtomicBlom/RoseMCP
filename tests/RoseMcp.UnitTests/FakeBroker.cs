using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A broker the relay can be pointed at in this process: its tools, its instructions and its
/// instance are the test's to choose, and killing it closes every connection the way a killed tray
/// closes its sockets.
/// <para>
/// Over pipes rather than a socket, so the relay's decisions -- reconnect or not, what the client is
/// told -- are tested with no process and no port, and the http half is left to the integration
/// suite, which kills a real broker.
/// </para>
/// </summary>
internal sealed class FakeBroker : IAsyncDisposable
{
	private readonly List<Connection> _connections = [];
	private readonly Lock _gate = new();
	private readonly IReadOnlyList<Tool> _tools;
	private readonly string? _instructions;
	private int _nextSession;
	private int _listings;
	private bool _refusing;

	public FakeBroker(IReadOnlyList<Tool> tools, string? instructions, string commit)
	{
		_tools = tools;
		_instructions = instructions;
		Build = new BuildIdentity { Version = "1.3.0", Commit = commit };
	}

	/// <summary>Different for every broker, as a real broker's is for every start.</summary>
	public string Instance { get; } = Guid.NewGuid().ToString("N");

	public BuildIdentity Build { get; }

	/// <summary>The relay protocol this broker says it speaks.</summary>
	public int RelayProtocol { get; init; } = Contracts.RelayProtocol.Version;

	/// <summary>The debug sessions open on this broker, as <c>rose_debug_list</c> reports them.</summary>
	public ConcurrentDictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

	/// <summary>Every tool called on this broker, in order.</summary>
	public ConcurrentQueue<string> Calls { get; } = new();

	/// <summary>How many tool listings this broker has answered.</summary>
	public int Listings => _listings;

	/// <summary>
	/// Whether every request is answered with a protocol error, as a broker that has restarted on the
	/// same endpoint answers a connection to the one before it: something answers, and it is not the
	/// broker the connection was opened to.
	/// </summary>
	public bool Refusing
	{
		get => Volatile.Read(ref _refusing);
		set => Volatile.Write(ref _refusing, value);
	}

	/// <summary>A tool the relay may re-send, as a broker's read-only tools are.</summary>
	public static Tool ReadOnly(string name, string description = "Reads something.") => new()
	{
		Name = name,
		Description = description,
		InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
		Annotations = new ToolAnnotations { ReadOnlyHint = true },
	};

	/// <summary>The tools a broker that can debug has, beside <paramref name="others"/>.</summary>
	public static IReadOnlyList<Tool> WithDebugging(params Tool[] others) =>
	[
		.. others,
		ReadOnly(ToolNames.DebugList, "Lists debug sessions."),
		new Tool
		{
			Name = ToolNames.DebugAttach,
			Description = "Attaches.",
			InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"processId":{"type":"integer"}}}""").RootElement.Clone(),
		},
	];

	/// <summary>Opens one session on this broker, as the relay's upstream does.</summary>
	public async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
	{
		var toServer = new Pipe();
		var toClient = new Pipe();

		var options = new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "fake-broker", Version = Build.ToHandshake() },
			ServerInstructions = _instructions,
			Handlers = new McpServerHandlers
			{
				ListToolsHandler = (_, _) =>
				{
					Interlocked.Increment(ref _listings);
					return ValueTask.FromResult(new ListToolsResult { Tools = [.. _tools] });
				},
				CallToolHandler = (context, _) => ValueTask.FromResult(Call(context.Params!)),
			},
		};

		options.Filters.Message.IncomingFilters.Add(next => async (context, token) =>
		{
			if (Refusing && context.JsonRpcMessage is JsonRpcRequest request)
			{
				throw new McpProtocolException($"No session answers {request.Method} here.", McpErrorCode.InvalidRequest);
			}

			await next(context, token);
		});

		var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
		var stop = new CancellationTokenSource();
		var running = server.RunAsync(stop.Token);

		var writes = new ClientWrites(toServer.Writer.AsStream());

		var client = await McpClient.CreateAsync(
			new StreamClientTransport(writes, toClient.Reader.AsStream()),
			cancellationToken: cancellationToken);

		lock (_gate)
		{
			_connections.Add(new Connection(server, stop, running, toClient, toServer, writes));
		}

		return client;
	}

	/// <summary>Closes every session, as a killed process does; the broker itself goes on accepting new ones.</summary>
	public async Task DropConnectionsAsync()
	{
		Connection[] open;
		lock (_gate)
		{
			open = [.. _connections];
			_connections.Clear();
		}

		foreach (var connection in open) await connection.CloseAsync();
	}

	public ValueTask DisposeAsync() => new(DropConnectionsAsync());

	private CallToolResult Call(CallToolRequestParams request)
	{
		Calls.Enqueue(request.Name);

		return request.Name switch
		{
			ToolNames.DebugAttach => Structured(new { sessionId = Attach() }),
			ToolNames.DebugList => Structured(new
			{
				sessions = Sessions.Select(session => new { sessionId = session.Key, targetDescription = session.Value, state = "Ready" }).ToArray(),
			}),
			_ => Structured(new { answer = request.Name }),
		};
	}

	private string Attach()
	{
		var id = $"session-{Interlocked.Increment(ref _nextSession):D4}";
		Sessions[id] = "Probe.exe";

		return id;
	}

	private static CallToolResult Structured(object value)
	{
		var element = JsonSerializer.SerializeToElement(value);

		return new CallToolResult
		{
			StructuredContent = element,
			Content = [new TextContentBlock { Text = element.GetRawText() }],
		};
	}

	private sealed record Connection(McpServer Server, CancellationTokenSource Stop, Task Running, Pipe ToClient, Pipe ToServer, ClientWrites Writes)
	{
		public async Task CloseAsync()
		{
			Writes.Sever();
			await Stop.CancelAsync();

			try
			{
				await Running;
			}
			catch (OperationCanceledException)
			{
			}

			// The client's read of the server's stream ends here, which is what a closed socket is to it.
			await ToClient.Writer.CompleteAsync();
			await ToServer.Reader.CompleteAsync();
			await Server.DisposeAsync();
			Stop.Dispose();
		}
	}

	/// <summary>
	/// What the client writes to the broker, refused once the connection is dropped, as a write to a
	/// closed socket is. Without it a call on a dropped connection waits for an answer that never comes,
	/// and the relay never learns from the call that the broker has gone.
	/// </summary>
	private sealed class ClientWrites(Stream inner) : Stream
	{
		private volatile bool _severed;

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public void Sever() => _severed = true;

		public override void Flush()
		{
			ThrowIfSevered();
			inner.Flush();
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			ThrowIfSevered();
			return inner.FlushAsync(cancellationToken);
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			ThrowIfSevered();
			inner.Write(buffer, offset, count);
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			ThrowIfSevered();
			return inner.WriteAsync(buffer, cancellationToken);
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing) inner.Dispose();
			base.Dispose(disposing);
		}

		private void ThrowIfSevered()
		{
			if (_severed) throw new IOException("The broker closed the connection.");
		}
	}
}
