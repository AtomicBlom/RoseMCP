using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// A note end to end: sent at <c>/operator/notes</c> with the operator token, carried on the next answer its session's
/// relay forwards, and answered through the same relay with <c>rose_reply</c> -- with real relays in front of a real
/// broker on a loopback port, each with an agent session in front of it, so the session a note reaches is the one
/// the broker tells apart by what its relay sends.
/// </summary>
public sealed class NoteRouteTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

	[Test]
	public async Task A_note_rides_on_its_sessions_next_answer_alone_and_is_answered_through_the_relay(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var mine = await tray.RelayAsync(cancellationToken);
		await using var theirs = await tray.RelayAsync(cancellationToken);

		await using var me = await RelayFront.OpenAsync(mine, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);
		await using var them = await RelayFront.OpenAsync(theirs, RelayFront.Options("claude-code", "2.1.301"), cancellationToken);

		await me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);
		await them.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);

		var rows = await tray.ClientsAsync(clients => clients.Count(client => client is { Named: true, ClientVersion: not null }) == 2, cancellationToken);
		var myRow = rows.Single(row => row.ClientVersion == "2.1.300");

		var sent = await Read<AgentNote>(
			await tray.Operator.PostAsJsonAsync("/operator/notes", Note(myRow.Id, "Why did this fail?"), ContractJson.Options, cancellationToken),
			cancellationToken);
		sent.Delivery.ShouldBe(NoteDelivery.Waiting);

		Text(await them.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken))
			.ShouldNotContain("RoseMCP window", customMessage: "another session's answer carries none of it");

		var carried = Text(await me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken));
		carried.ShouldContain("A note from the person at the RoseMCP window");
		carried.ShouldContain("\"Why did this fail?\"");
		carried.ShouldContain(sent.Id);

		Text(await me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken))
			.ShouldNotContain("RoseMCP window", customMessage: "a note rides on one answer");

		var delivered = (await tray.NotesAsync(cancellationToken)).Notes.ShouldHaveSingleItem();
		delivered.Delivery.ShouldBe(NoteDelivery.Delivered);
		delivered.DeliveredTool.ShouldBe(Echo.Name);
		delivered.DeliveredWith.ShouldNotBeNull("the call that carried it is in the activity record");

		var refused = await them.Agent.CallToolAsync(
			ToolNames.Reply,
			new Dictionary<string, object?> { ["noteId"] = sent.Id, ["text"] = "Not mine to answer." },
			cancellationToken: cancellationToken);
		refused.IsError.ShouldBe(true, "another session cannot answer it");
		Text(refused).ShouldContain("No note");

		var answered = await me.Agent.CallToolAsync(
			ToolNames.Reply,
			new Dictionary<string, object?> { ["noteId"] = sent.Id, ["text"] = "The width was never set." },
			cancellationToken: cancellationToken);
		answered.IsError.ShouldNotBe(true, Text(answered));
		answered.StructuredContent.ShouldNotBeNull().GetProperty("replies").GetInt32().ShouldBe(1);

		var reply = (await tray.NotesAsync(cancellationToken)).Notes.ShouldHaveSingleItem().Replies.ShouldHaveSingleItem();
		reply.Text.ShouldBe("The width was never set.");
	}

	/// <summary>A note reaches an agent as the person's words, so sending or reading one needs the person's token.</summary>
	[Test]
	[Arguments("GET")]
	[Arguments("POST")]
	public async Task Notes_need_the_operator_token(string method, CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		using var request = new HttpRequestMessage(new HttpMethod(method), "/operator/notes")
		{
			Content = method == "POST" ? JsonContent.Create(Note("client-00000000", "Hello"), options: ContractJson.Options) : null,
		};

		using var response = await tray.Anonymous.SendAsync(request, cancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		tray.Services.GetRequiredService<AgentNotes>().Describe().Notes.ShouldBeEmpty();
	}

	/// <summary>
	/// The refusals a window can act on are told apart: a client that is gone is not found, and the unnamed row -- every
	/// client without a relay -- is a request to fix, saying why.
	/// </summary>
	[Test]
	public async Task A_note_it_cannot_take_says_why(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);

		await using var direct = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = tray.Endpoint }),
			new McpClientOptions
			{
				DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
				ClientInfo = new Implementation { Name = "direct-client", Version = "0.1" },
			},
			cancellationToken: cancellationToken);
		await direct.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);

		var unnamed = (await tray.ClientsAsync(clients => clients.Count > 0, cancellationToken)).Single(row => !row.Named);

		using var toUnnamed = await tray.Operator.PostAsJsonAsync("/operator/notes", Note(unnamed.Id, "Hello"), ContractJson.Options, cancellationToken);
		toUnnamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await toUnnamed.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken))!.Message.ShouldContain("relay");

		using var toNobody = await tray.Operator.PostAsJsonAsync("/operator/notes", Note("client-00000000", "Hello"), ContractJson.Options, cancellationToken);
		toNobody.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>A window waiting on the notes is answered when one is sent, not when its wait runs out.</summary>
	[Test]
	public async Task A_wait_on_the_notes_is_answered_when_one_is_sent(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var mine = await tray.RelayAsync(cancellationToken);
		await using var me = await RelayFront.OpenAsync(mine, RelayFront.Options("claude-code", "2.1.300"), cancellationToken);
		await me.Agent.CallToolAsync(Echo.Name, cancellationToken: cancellationToken);

		var row = (await tray.ClientsAsync(clients => clients.Any(client => client.Named), cancellationToken)).Single(client => client.Named);
		var before = await tray.NotesAsync(cancellationToken);

		var started = DateTime.UtcNow;
		var waiting = tray.Operator.GetFromJsonAsync<NoteList>(
			$"/operator/notes?after={before.Version}&waitSeconds=30", ContractJson.Options, cancellationToken);

		await tray.Operator.PostAsJsonAsync("/operator/notes", Note(row.Id, "Look at this"), ContractJson.Options, cancellationToken);

		var answered = await waiting;
		answered.ShouldNotBeNull().Notes.ShouldHaveSingleItem().Text.ShouldBe("Look at this");
		(DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
	}

	/// <summary>
	/// Only an answer delivers. A call the tool refuses by throwing, one answered with an error, and one its caller
	/// cancelled -- whose tool answers anyway -- each leave the note waiting, with nothing of it in what came back; the
	/// next answer carries it. Driven as a relay drives the broker, its session named in every call and a cancellation
	/// sent to the far side before the wait is given up.
	/// </summary>
	[Test]
	public async Task A_refused_failed_or_cancelled_call_leaves_the_note_for_the_next_answer(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var tray = await Tray.StartAsync(home, cancellationToken);
		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = tray.Endpoint }),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		var session = CallSession.Mint();
		Task<CallToolResult> Call(string tool, Dictionary<string, object?>? arguments = null, CancellationToken? token = null) =>
			CancellableToolCall.InvokeAsync(agent, tool, arguments ?? [], progress: null, token ?? cancellationToken, session: session);

		await Call(Echo.Name);
		var row = (await tray.ClientsAsync(clients => clients.Any(client => client.Named), cancellationToken)).Single(client => client.Named);
		var sent = await Read<AgentNote>(
			await tray.Operator.PostAsJsonAsync("/operator/notes", Note(row.Id, "Only an answer carries this"), ContractJson.Options, cancellationToken),
			cancellationToken);

		var refused = await Call(ToolNames.Reply, new() { ["noteId"] = "note-0000000000000000", ["text"] = "Which note?" });
		refused.IsError.ShouldBe(true, "the tool threw");
		Text(refused).ShouldNotContain(sent.Id);
		await StillWaitingAsync();

		var failed = await Call(Failing.Name);
		failed.IsError.ShouldBe(true, "the tool answered with an error");
		Text(failed).ShouldNotContain(sent.Id);
		await StillWaitingAsync();

		Stubborn.Reset();
		using (var givingUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			var stubborn = Call(Stubborn.Name, token: givingUp.Token);
			await Stubborn.Started.Task.WaitAsync(Patience, cancellationToken);
			await givingUp.CancelAsync();

			await Should.ThrowAsync<OperationCanceledException>(stubborn);
			await Stubborn.Answered.Task.WaitAsync(Patience, cancellationToken);
		}

		// The record finishes the call once every filter has, so a finished entry means the filter has had its turn.
		await tray.FinishedAsync(Stubborn.Name, cancellationToken);
		await StillWaitingAsync();

		var carried = await Call(Echo.Name);
		Text(carried).ShouldContain(sent.Id);
		(await tray.NotesAsync(cancellationToken)).Notes.ShouldHaveSingleItem().DeliveredTool.ShouldBe(Echo.Name);

		async Task StillWaitingAsync() =>
			(await tray.NotesAsync(cancellationToken)).Notes.ShouldHaveSingleItem().Delivery.ShouldBe(NoteDelivery.Waiting);
	}

	private static NoteRequest Note(string client, string text) => new()
	{
		Client = client,
		Text = text,
		Subject = new NoteSubject
		{
			Kind = NoteSubjectKind.ActivityCall,
			Readable = "probe_echo at 14:05:31",
			ActivityEntry = "0123456789abcdef",
		},
	};

	/// <summary>Every text block of an answer, as a client that reads text sees it.</summary>
	private static string Text(CallToolResult result) =>
		string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

	private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

			return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken))!;
		}
	}

	/// <summary>A tool to call, standing in for the broker's own, which need a worker this suite does not start.</summary>
	[McpServerToolType]
	public sealed class Echo
	{
		public const string Name = "probe_echo";

		[McpServerTool(Name = Name), Description("Answers at once.")]
		public static string Answer() => "echo";
	}

	/// <summary>A tool that answers with an error rather than throwing one.</summary>
	[McpServerToolType]
	public sealed class Failing
	{
		public const string Name = "probe_failing";

		[McpServerTool(Name = Name), Description("Answers with an error, without throwing.")]
		public static CallToolResult Answer() => new() { IsError = true, Content = [new TextContentBlock { Text = "It went wrong." }] };
	}

	/// <summary>
	/// A tool that waits until its call is cancelled and then answers anyway, which is the case the filter's own check of
	/// the call's token is for. Its signals are for the one test that calls it.
	/// </summary>
	[McpServerToolType]
	public sealed class Stubborn
	{
		public const string Name = "probe_stubborn";

		/// <summary>Completed once a call has started waiting.</summary>
		public static TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completed once a call has answered after being cancelled.</summary>
		public static TaskCompletionSource Answered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public static void Reset()
		{
			Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		[McpServerTool(Name = Name), Description("Waits until it is cancelled, then answers anyway.")]
		public static async Task<string> Answer(CancellationToken cancellationToken)
		{
			Started.TrySetResult();

			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Answers anyway, as a tool that does not honour its token would.
			}

			Answered.TrySetResult();
			return "answered anyway";
		}
	}

	/// <summary>A broker serving MCP, the admin routes and the operator API over http on a loopback port, as the tray does.</summary>
	private sealed class Tray : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Tray(WebApplication app, OperatorToken token)
		{
			_app = app;

			var address = new Uri(app.Urls.First() + "/");
			Endpoint = address;
			Anonymous = new HttpClient { BaseAddress = address };
			Operator = new HttpClient { BaseAddress = address };
			Operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
		}

		public Uri Endpoint { get; }

		public HttpClient Anonymous { get; }

		public HttpClient Operator { get; }

		public IServiceProvider Services => _app.Services;

		public static async Task<Tray> StartAsync(TemporaryLocalAppData home, CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services
				.AddRoseMcpBroker(broker =>
				{
					broker.LocalAppData = home.Path;
					broker.ActivityDirectory = Path.Combine(home.Path, "Activity");
				})
				.WithTools<Echo>()
				.WithTools<Failing>()
				.WithTools<Stubborn>()
				.WithHttpTransport();

			var app = builder.Build();
			var token = OperatorToken.Mint();
			app.MapMcp();
			app.MapRoseAdminApi();
			app.MapRoseOperatorApi(token);

			await app.StartAsync(cancellationToken);

			return new Tray(app, token);
		}

		/// <summary>A relay in front of this broker, given a few attempts, since a loaded runner can overrun its two-second handshake.</summary>
		public async Task<TrayRelay> RelayAsync(CancellationToken cancellationToken)
		{
			var options = new ServerOptions { Host = Endpoint.Host, Port = Endpoint.Port };

			for (var attempt = 0; attempt < 3; attempt++)
			{
				if (await TrayRelay.TryConnectAsync(options, NullLoggerFactory.Instance, cancellationToken) is { } relay) return relay;
			}

			throw new InvalidOperationException($"No relay could reach the broker at {Endpoint} in three attempts.");
		}

		/// <summary>The client table, read until <paramref name="settled"/> holds of it: introductions arrive on their own time.</summary>
		public async Task<IReadOnlyList<ClientSummary>> ClientsAsync(
			Func<IReadOnlyList<ClientSummary>, bool> settled,
			CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + Patience;

			while (true)
			{
				var list = await Operator.GetFromJsonAsync<ClientList>("/operator/clients", ContractJson.Options, cancellationToken);
				var clients = list?.Clients ?? [];
				if (settled(clients)) return clients;

				if (DateTime.UtcNow > deadline)
				{
					throw new ShouldAssertException(
						$"The clients never settled; the last read was {JsonSerializer.Serialize(clients, ContractJson.Options)}");
				}

				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			}
		}

		public async Task<NoteList> NotesAsync(CancellationToken cancellationToken) =>
			(await Operator.GetFromJsonAsync<NoteList>("/operator/notes", ContractJson.Options, cancellationToken))!;

		/// <summary>Waits until the activity record holds a finished call of <paramref name="tool"/>.</summary>
		public async Task FinishedAsync(string tool, CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + Patience;

			while (true)
			{
				var page = await Operator.GetFromJsonAsync<ActivityPage>(
					$"/operator/activity?tool={Uri.EscapeDataString(tool)}", ContractJson.Options, cancellationToken);
				if (page!.Entries.Any(entry => entry.Outcome != ActivityOutcome.Running)) return;

				if (DateTime.UtcNow > deadline) throw new ShouldAssertException($"No call of {tool} finished in the record.");

				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			}
		}

		public async ValueTask DisposeAsync()
		{
			Anonymous.Dispose();
			Operator.Dispose();

			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}
}
