using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;
using static RoseMcp.IntegrationTests.ProbeTargetSession;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The http surface a person's tools read a broker through, over a real server process.
/// <para>
/// A real process because the thing being tested is the arrangement rather than the handlers: which
/// requests the token refuses, and -- the point of the whole surface -- that a session an MCP client
/// started is visible here. An in-process host would exercise the handlers and answer neither, since
/// both turn on how a request arrives.
/// </para>
/// <para>
/// The token is chosen rather than discovered. A server with no <c>ROSEMCP_TOKEN</c> mints one and
/// says so only in its log, which is the right behaviour and no use to a test.
/// </para>
/// </summary>
public sealed class OperatorApiTests
{
	private const string Token = "a-token-only-this-test-knows";

	/// <summary>
	/// Long enough for a cold server to bind on a loaded machine, and bounded: a fixture check that
	/// can hang is worse than the bug it looks for.
	/// </summary>
	private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

	[Test]
	public async Task Refuses_a_request_with_no_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Anonymous.GetAsync(broker.Url("/operator/sessions"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Refuses_a_request_with_the_wrong_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var request = new HttpRequestMessage(HttpMethod.Get, broker.Url("/operator/sessions"));
		request.Headers.Add("Authorization", "Bearer not-the-token");

		using var answer = await broker.Anonymous.SendAsync(request, cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// Hello names the broker's build by its commit, which is this suite's own: the server was built
	/// from the same tree in the same build. For a local build it also counts how far the checkout has
	/// moved on, which git can always do for the commit the checkout was built at.
	/// </summary>
	[Test]
	public async Task Hello_names_the_brokers_build_by_its_commit()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		var hello = await broker.Client.GetFromJsonAsync<OperatorHello>(
			broker.Url("/operator/hello"), ContractJson.Options, cancellationToken);

		var ours = BuildIdentity.Of(typeof(OperatorApiTests).Assembly);

		hello.ShouldNotBeNull();
		hello!.Build.Commit.ShouldNotBeNull();
		hello.Build.Commit.ShouldBe(ours.Commit);
		hello.Build.BuiltUtc.ShouldNotBeNull();
		hello.Build.Checkout.ShouldBe(ours.Checkout);

		if (ours.Checkout is null)
		{
			hello.Checkout.ShouldBeNull("a build that names no checkout has nothing to count from");
			return;
		}

		hello.Checkout.ShouldNotBeNull();
		hello.Checkout!.Head.Count.ShouldNotBeNull(hello.Checkout.Head.Unknown);
	}

	/// <summary>Hello is behind the token like everything else here: a build's checkout path is the machine's business.</summary>
	[Test]
	public async Task Hello_needs_the_token()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Anonymous.GetAsync(broker.Url("/operator/hello"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Lists_no_sessions_on_a_broker_nobody_is_debugging_through()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		var sessions = await broker.Client.GetFromJsonAsync<OperatorSessionList>(
			broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

		sessions.ShouldNotBeNull();
		sessions!.Sessions.ShouldBeEmpty();
		sessions.Version.ShouldNotBeNullOrWhiteSpace();
	}

	/// <summary>
	/// A session that is not there is a 404 carrying the sentence that says what to do about it, not
	/// a bare status. The message is the contract: a caller told only that something failed has to
	/// guess, and the guesses are expensive.
	/// </summary>
	[Test]
	public async Task Says_which_sessions_there_are_when_asked_for_one_that_is_gone()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var answer = await broker.Client.GetAsync(
			broker.Url("/operator/sessions/session-nothere"), cancellationToken);

		answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		var error = await answer.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken);

		error.ShouldNotBeNull();
		error!.Message.ShouldContain("session-nothere", Case.Sensitive);
		error.Message.ShouldContain("/operator/sessions", Case.Sensitive);
	}

	/// <summary>
	/// The whole reason this surface exists. A session started by an MCP client belongs to that
	/// client, and every agent-facing debug tool refuses it to anyone else -- which is right, and
	/// which would make a window showing sessions impossible, because inside an http endpoint there
	/// is no MCP session to be the owner. So the operator path resolves sessions without the
	/// ownership check, and this is what says it does.
	/// <para>
	/// The session is started through a real http MCP client, which is what an agent talking to a
	/// tray is. That matters: it gives the session a transport session id as its owner, and a null
	/// owner would match what an endpoint sees and prove nothing.
	/// </para>
	/// </summary>
	[Test]
	public async Task Shows_a_session_an_mcp_client_started_and_owns()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var target = StartProbeTarget();
		try
		{
			await using var agent = await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = new Uri(broker.Url("/")),
					AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
				}),

				// A server/discover probe the client gives up on itself falls back to initialize with the probe's
				// protocol version still in the http header, and the broker refuses the mismatch -- which a full
				// suite's load reaches inside the probe's five seconds. The broker's own children are held to the
				// same rule.
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

			var attached = await agent.CallToolAsync(
				ToolNames.DebugAttach,
				new Dictionary<string, object?> { ["processId"] = target.Id },
				cancellationToken: cancellationToken);

			var sessionId = attached.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options)?.SessionId;
			sessionId.ShouldNotBeNull();

			var listed = await broker.Client.GetFromJsonAsync<OperatorSessionList>(
				broker.Url("/operator/sessions"), ContractJson.Options, cancellationToken);

			listed.ShouldNotBeNull();
			var row = listed!.Sessions.ShouldHaveSingleItem();
			row.SessionId.ShouldBe(sessionId);

			// What the session holds is read apart from Rose's own: its host is Rose's cost, the app it debugs is
			// not. The app is read only while the host says it is serving, so wait for the host to say so.
			var memory = await SessionMemoryAsync(broker, sessionId!, cancellationToken);
			memory.Host.ShouldNotBeNull().ProcessId.ShouldBe(row.HostProcessId!.Value);
			memory.Target.ShouldNotBeNull().ProcessId.ShouldBe(target.Id);
			memory.Target.WorkingSetBytes.ShouldBeGreaterThan(0);

			// And reachable one at a time, which is what every other route depends on.
			using var one = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			one.StatusCode.ShouldBe(HttpStatusCode.OK);

			// Detached from here too, leaving the target running: an operator holds the whole machine's
			// sessions, so ending one is theirs to do.
			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{sessionId}/detach"), content: null, cancellationToken);

			detached.StatusCode.ShouldBe(HttpStatusCode.OK);

			var closed = await detached.Content.ReadFromJsonAsync<LiveSessionClosed>(
				ContractJson.Options, cancellationToken);

			closed.ShouldNotBeNull();
			closed!.Closed.ShouldBeTrue("the operator path closes a session an agent started");
			target.HasExited.ShouldBeFalse("a detach leaves the target running");

			// Gone by name, which is the other half of a close.
			using var again = await broker.Client.GetAsync(broker.Url($"/operator/sessions/{sessionId}"), cancellationToken);
			again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// An operator request is a call entering Rose, so it is given an id the way a tool call is, and the
	/// hop it makes to the live-app host carries it. Found from the host's side: the line the host writes
	/// for the detach names an id, and that id is on the broker's lines too -- which it is only if the
	/// broker minted it and sent it, since a host sent none mints one nobody else has.
	/// </summary>
	[Test]
	public async Task An_operator_request_carries_one_id_to_the_host_it_reaches()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using var target = StartProbeTarget();
		try
		{
			await using var agent = await McpClient.CreateAsync(
				new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = new Uri(broker.Url("/")),
					AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
				}),
				new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
				cancellationToken: cancellationToken);

			var attached = await agent.CallToolAsync(
				ToolNames.DebugAttach,
				new Dictionary<string, object?> { ["processId"] = target.Id },
				cancellationToken: cancellationToken);

			var summary = attached.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options);
			summary.ShouldNotBeNull();
			var hostLog = summary!.HostLogPath;
			hostLog.ShouldNotBeNull("the host names the log it writes");

			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{summary.SessionId}/detach"), content: null, cancellationToken);
			detached.StatusCode.ShouldBe(HttpStatusCode.OK);

			var detachLine = Lines(hostLog)
			.Where(line => line.Contains($"Detached from pid {target.Id}", StringComparison.Ordinal))
			.ToList()
			.ShouldHaveSingleItem();
			var id = detachLine.Split(' ')[3];

			CallCorrelation.IsWellFormed(id).ShouldBeTrue($"the host's detach line names no call: {detachLine}");

			var serverLogs = Directory.GetFiles(
				Path.Combine(Environment.GetEnvironmentVariable(RoseLogFile.RootVariable)!, "Server"), "*.log");

			serverLogs.SelectMany(Lines).ShouldContain(
				line => line.Contains($"] {id} ", StringComparison.Ordinal),
				$"no broker line carries {id}, so the host minted it rather than being sent it");
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A workspace an agent opens shows in every view a window reads, through the real broker: the workspace list,
	/// answered as soon as it appears to a window already waiting on it, with its folder, what it was loaded under and
	/// when it closes; the memory, with its worker beside the broker's own process; the activity record, by workspace,
	/// failure and text, a page at a time; and the client table, with the agent's calls counted.
	/// </summary>
	[Test]
	public async Task A_workspace_an_agent_opens_shows_in_every_view_a_window_reads()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var before = await broker.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken);
		before.Workspaces.ShouldBeEmpty();
		before.IdleEvictionAfter.ShouldNotBeNull("an http broker closes an idle workspace");

		// Held open from before the open, under the client's own timeout.
		var waiting = broker.GetAsync<OperatorWorkspaceList>(
			$"/operator/workspaces?after={before.Version}&waitSeconds=20", cancellationToken);

		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(broker.Url("/")),
				AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
			}),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		await agent.CallToolAsync(
			ToolNames.WorkspaceOpen,
			new Dictionary<string, object?> { ["workspace"] = fixture.SolutionPath },
			cancellationToken: cancellationToken);

		var appeared = await waiting;
		appeared.Version.ShouldNotBe(before.Version);
		appeared.Workspaces.ShouldHaveSingleItem().Summary.Workspace.ShouldBe(fixture.SolutionPath);

		// Status waits for the load, so what the views say next is about a loaded workspace.
		for (var asked = 0; asked < 2; asked++)
		{
			var status = await agent.CallToolAsync(
				ToolNames.WorkspaceStatus,
				new Dictionary<string, object?> { ["workspace"] = fixture.SolutionPath },
				cancellationToken: cancellationToken);
			status.IsError.ShouldNotBe(true);
		}

		var loaded = (await broker.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken)).Workspaces.ShouldHaveSingleItem();
		loaded.Summary.State.ShouldBe(WorkspaceState.Loaded);
		loaded.Folder.ShouldBe(Path.GetDirectoryName(fixture.SolutionPath));
		loaded.Configuration.ShouldBe("Debug");
		loaded.Platform.ShouldNotBeNullOrWhiteSpace();
		loaded.Summary.WorkingSetBytes.ShouldNotBeNull().ShouldBeGreaterThan(0);
		loaded.InUse.ShouldBeFalse("nothing is running on it");
		loaded.ClosesAtUtc.ShouldBe(loaded.LastUsedUtc + appeared.IdleEvictionAfter!.Value);

		var memory = await broker.GetAsync<MemoryReport>("/operator/memory", cancellationToken);
		memory.Broker.ProcessId.ShouldNotBe(Environment.ProcessId, "the broker is the server process, not the test");
		var worker = memory.Workers.ShouldHaveSingleItem();
		worker.Workspace.ShouldBe(fixture.SolutionPath);
		worker.Process.ShouldNotBeNull().ProcessId.ShouldBe(loaded.Summary.ProcessId!.Value);
		memory.Machine.ShouldNotBeNull().TotalBytes.ShouldBeGreaterThan(worker.Process.WorkingSetBytes);

		var workspace = Uri.EscapeDataString(fixture.SolutionPath);
		var activity = await broker.GetAsync<ActivityPage>($"/operator/activity?workspace={workspace}", cancellationToken);
		activity.Entries.Select(entry => entry.Tool).ShouldContain(ToolNames.WorkspaceStatus);

		var page = await broker.GetAsync<ActivityPage>($"/operator/activity?workspace={workspace}&limit=1", cancellationToken);
		page.Entries.ShouldHaveSingleItem();
		page.Next.ShouldNotBeNull("both status calls matched");
		var older = await broker.GetAsync<ActivityPage>(
			$"/operator/activity?workspace={workspace}&limit=1&before={Uri.EscapeDataString(page.Next)}", cancellationToken);
		older.Entries.ShouldHaveSingleItem().Id.ShouldNotBe(page.Entries[0].Id);

		(await broker.GetAsync<ActivityPage>($"/operator/activity?workspace={workspace}&failures=true", cancellationToken))
			.Entries.ShouldBeEmpty();
		(await broker.GetAsync<ActivityPage>($"/operator/activity?text={Uri.EscapeDataString(Path.GetFileName(fixture.Root))}", cancellationToken))
			.Entries.ShouldNotBeEmpty("the workspace's path names the copy's folder");

		var clients = await broker.GetAsync<ClientList>("/operator/clients", cancellationToken);
		clients.Clients.ShouldContain(client => client.Calls >= 2);
	}

	/// <summary>
	/// A note rides on a real call through the real broker: sent at <c>/operator/notes</c> to a session its relay
	/// names, it is in the notices of that session's next answer -- a workspace's status, from a real worker -- and of
	/// no answer after it; the call that carried it holds it in its record; and the agent's <c>rose_reply</c> shows
	/// under it.
	/// </summary>
	[Test]
	public async Task A_note_rides_on_its_sessions_next_real_call_and_is_answered()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(broker.Url("/")),
				AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
			}),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		// What a relay sends with every call, naming the session it serves.
		var session = new RequestOptions { Meta = new JsonObject { [CallSession.MetaKey] = CallSession.Mint() } };
		var workspace = new Dictionary<string, object?> { ["workspace"] = fixture.SolutionPath };

		await agent.CallToolAsync(ToolNames.WorkspaceOpen, workspace, options: session, cancellationToken: cancellationToken);

		var row = (await broker.GetAsync<ClientList>("/operator/clients", cancellationToken)).Clients.Single(client => client.Named);

		using var posted = await broker.Client.PostAsJsonAsync(
			broker.Url("/operator/notes"),
			new NoteRequest
			{
				Client = row.Id,
				Text = "Is Simple loading the configuration you expect?",
				Subject = new NoteSubject
				{
					Kind = NoteSubjectKind.WorkspaceReason,
					Readable = "Simple's configuration",
					Workspace = fixture.SolutionPath,
					ReasonKind = ReasonKinds.WrongPlatform,
				},
			},
			ContractJson.Options,
			cancellationToken);
		posted.StatusCode.ShouldBe(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync(cancellationToken));
		var note = (await posted.Content.ReadFromJsonAsync<AgentNote>(ContractJson.Options, cancellationToken)).ShouldNotBeNull();

		var carried = await agent.CallToolAsync(ToolNames.WorkspaceStatus, workspace, options: session, cancellationToken: cancellationToken);
		carried.IsError.ShouldNotBe(true);
		Notices(carried).ShouldContain(notice => notice.Contains(note.Id, StringComparison.Ordinal), "the session's next answer carries it");

		var after = await agent.CallToolAsync(ToolNames.WorkspaceStatus, workspace, options: session, cancellationToken: cancellationToken);
		Notices(after).ShouldNotContain(notice => notice.Contains(note.Id, StringComparison.Ordinal), "a note rides on one answer");

		var answered = await agent.CallToolAsync(
			ToolNames.Reply,
			new Dictionary<string, object?> { ["noteId"] = note.Id, ["text"] = "Debug, as the solution declares." },
			options: session,
			cancellationToken: cancellationToken);
		answered.IsError.ShouldNotBe(true);

		var held = (await broker.GetAsync<NoteList>("/operator/notes", cancellationToken)).Notes.ShouldHaveSingleItem();
		held.Delivery.ShouldBe(NoteDelivery.Delivered);
		held.DeliveredTool.ShouldBe(ToolNames.WorkspaceStatus);
		held.Replies.ShouldHaveSingleItem().Text.ShouldBe("Debug, as the solution declares.");

		var entry = await broker.GetAsync<ActivityEntry>($"/operator/activity/{held.DeliveredWith.ShouldNotBeNull()}", cancellationToken);
		entry.Tool.ShouldBe(ToolNames.WorkspaceStatus);
		entry.Response.ShouldNotBeNull().ShouldContain(note.Id, customMessage: "the record holds the answer the agent was given, the note in it");
	}

	/// <summary>
	/// A session a person starts is theirs until they share it, end to end over a real broker, a real host and a real
	/// target: attached at <c>/operator/debug/attach</c>, it is reached by no agent session -- not listed, not read, and
	/// its process not attached to again; shared with one relayed session, that session lists it, reads it and is
	/// handed it by its own attach, while another does not; the agent's detach ends the share and leaves the person's
	/// debugger attached; unshared, it is the person's alone again; and a second attach from a window opens it rather
	/// than starting another debugger.
	/// </summary>
	[Test]
	[NotInParallel(RecentTargetsKept.Key)]
	public async Task A_session_a_person_starts_is_theirs_until_they_share_it_with_one_agent_session()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var recent = new RecentTargetsKept();
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);
		await using var agent = await AgentAsync(broker, cancellationToken);

		var mine = new RequestOptions { Meta = new JsonObject { [CallSession.MetaKey] = CallSession.Mint() } };
		var theirs = new RequestOptions { Meta = new JsonObject { [CallSession.MetaKey] = CallSession.Mint() } };

		using var target = StartProbeTarget();
		try
		{
			(await ListAsync(agent, mine, cancellationToken)).ShouldBeEmpty();
			(await ListAsync(agent, theirs, cancellationToken)).ShouldBeEmpty();
			var clients = (await broker.GetAsync<ClientList>("/operator/clients", cancellationToken)).Clients.Where(client => client.Named).ToList();
			clients.Count.ShouldBe(2);

			var started = await PostAsync<DebugStart>(broker, "/operator/debug/attach", new AttachRequest { ProcessId = target.Id }, cancellationToken);
			started.Existing.ShouldBeFalse();
			started.Session.StartedBy.ShouldBe(LiveAppStarter.Person);
			started.Session.TargetProcessId.ShouldBe(target.Id);
			var sessionId = started.Session.SessionId;

			(await ListAsync(agent, mine, cancellationToken)).ShouldBeEmpty("a person's session is theirs alone");
			var unreached = await agent.CallToolAsync(
				ToolNames.DebugEvents, new Dictionary<string, object?> { ["sessionId"] = sessionId }, options: mine, cancellationToken: cancellationToken);
			unreached.IsError.ShouldBe(true);

			var refused = await agent.CallToolAsync(
				ToolNames.DebugAttach, new Dictionary<string, object?> { ["processId"] = target.Id }, options: mine, cancellationToken: cancellationToken);
			refused.IsError.ShouldBe(true);
			Text(refused).ShouldContain("already being debugged");

			// Shared with the session that listed first, by the row the client table gives it.
			var myRow = await RowOfAsync(broker, agent, mine, clients, cancellationToken);
			var shared = await PostAsync<LiveAppSessionSummary>(broker, $"/operator/sessions/{sessionId}/share", new ShareRequest { Client = myRow }, cancellationToken);
			shared.SharedWith.ShouldNotBeNull().ClientId.ShouldBe(myRow);
			shared.SharedWith.Connected.ShouldBe(true);

			var listed = (await ListAsync(agent, mine, cancellationToken)).ShouldHaveSingleItem();
			listed.SessionId.ShouldBe(sessionId);
			listed.StartedBy.ShouldBe(LiveAppStarter.Person);
			(await ListAsync(agent, theirs, cancellationToken)).ShouldBeEmpty("shared with one session, not every one");

			var read = await agent.CallToolAsync(
				ToolNames.DebugEvents, new Dictionary<string, object?> { ["sessionId"] = sessionId }, options: mine, cancellationToken: cancellationToken);
			read.IsError.ShouldNotBe(true, Text(read));

			var handed = await agent.CallToolAsync(
				ToolNames.DebugAttach, new Dictionary<string, object?> { ["processId"] = target.Id }, options: mine, cancellationToken: cancellationToken);
			handed.IsError.ShouldNotBe(true, Text(handed));
			Summary(handed).SessionId.ShouldBe(sessionId, "a process has one debugger, so its attach is handed the session");

			var left = await agent.CallToolAsync(
				ToolNames.DebugDetach, new Dictionary<string, object?> { ["sessionId"] = sessionId }, options: mine, cancellationToken: cancellationToken);
			left.IsError.ShouldNotBe(true, Text(left));
			left.StructuredContent?.Deserialize<LiveSessionDetached>(ContractJson.Options).ShouldNotBeNull().Detached.ShouldBeFalse();
			(await ListAsync(agent, mine, cancellationToken)).ShouldBeEmpty("leaving ends the share");
			var still = await broker.GetAsync<LiveAppSessionSummary>($"/operator/sessions/{sessionId}", cancellationToken);
			still.SharedWith.ShouldBeNull();
			still.State.ShouldBe(LiveAppSessionState.Ready, "the person's debugger stays attached");

			await PostAsync<LiveAppSessionSummary>(broker, $"/operator/sessions/{sessionId}/share", new ShareRequest { Client = myRow }, cancellationToken);
			(await ListAsync(agent, mine, cancellationToken)).ShouldHaveSingleItem();
			using (var unshared = await broker.Client.DeleteAsync(broker.Url($"/operator/sessions/{sessionId}/share"), cancellationToken))
			{
				unshared.StatusCode.ShouldBe(HttpStatusCode.OK);
			}

			(await ListAsync(agent, mine, cancellationToken)).ShouldBeEmpty();

			var again = await PostAsync<DebugStart>(broker, "/operator/debug/attach", new AttachRequest { ProcessId = target.Id }, cancellationToken);
			again.Existing.ShouldBeTrue();
			again.Session.SessionId.ShouldBe(sessionId);

			var apps = await broker.GetAsync<RunningAppList>("/operator/debug/apps?every=true", cancellationToken);
			apps.Apps.ShouldContain(app => app.ProcessId == target.Id && app.SessionId == sessionId && app.SessionStartedBy == LiveAppStarter.Person);

			var remembered = (await broker.GetAsync<RecentTargetList>("/operator/debug/recent", cancellationToken)).Targets.First();
			remembered.Kind.ShouldBe(LiveAppTargetKind.AttachProcess);
			remembered.ExecutablePath.ShouldBe(target.MainModule!.FileName, StringCompareShould.IgnoreCase);

			using var detached = await broker.Client.PostAsync(broker.Url($"/operator/sessions/{sessionId}/detach"), content: null, cancellationToken);
			(await detached.Content.ReadFromJsonAsync<LiveSessionClosed>(ContractJson.Options, cancellationToken)).ShouldNotBeNull().Closed.ShouldBeTrue();
			target.HasExited.ShouldBeFalse("a detach leaves the target running");
		}
		finally
		{
			if (!target.HasExited) target.Kill(entireProcessTree: true);
		}
	}

	/// <summary>
	/// A person launches a program by its full path with its arguments, and the session is theirs; a program named
	/// by a relative path is refused before any host starts, since a window has no folder to measure one from.
	/// </summary>
	[Test]
	[NotInParallel(RecentTargetsKept.Key)]
	public async Task A_person_launches_a_program_by_its_full_path()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var recent = new RecentTargetsKept();
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);

		using (var relative = await broker.Client.PostAsJsonAsync(
			broker.Url("/operator/debug/launch"), new LaunchRequest { ExecutablePath = "DebugProbeTarget.exe" }, ContractJson.Options, cancellationToken))
		{
			relative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		var started = await PostAsync<DebugStart>(
			broker, "/operator/debug/launch", new LaunchRequest { ExecutablePath = ProbeTargetPath(), Arguments = "--from-a-window" }, cancellationToken);

		var launched = started.Session.TargetProcessId;
		try
		{
			started.Session.StartedBy.ShouldBe(LiveAppStarter.Person);
			started.Session.State.ShouldBe(LiveAppSessionState.Ready, started.Session.Detail);
			launched.ShouldNotBeNull();

			var remembered = (await broker.GetAsync<RecentTargetList>("/operator/debug/recent", cancellationToken)).Targets.First();
			remembered.Kind.ShouldBe(LiveAppTargetKind.LaunchExecutable);
			remembered.Arguments.ShouldBe("--from-a-window");

			using var detached = await broker.Client.PostAsync(
				broker.Url($"/operator/sessions/{started.Session.SessionId}/detach"), content: null, cancellationToken);
			detached.StatusCode.ShouldBe(HttpStatusCode.OK);
		}
		finally
		{
			if (launched is { } pid)
			{
				try
				{
					using var process = System.Diagnostics.Process.GetProcessById(pid);
					process.Kill(entireProcessTree: true);
				}
				catch (ArgumentException)
				{
					// Already gone.
				}
			}
		}
	}

	/// <summary>An MCP client of the broker with no relay in front of it, naming its session in each call as a relay would.</summary>
	private static async Task<McpClient> AgentAsync(OperatorBroker broker, CancellationToken cancellationToken) =>
		await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(broker.Url("/")),
				AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
			}),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

	/// <summary>What <c>rose_debug_list</c> answers the session <paramref name="session"/> names.</summary>
	private static async Task<IReadOnlyList<LiveAppSessionSummary>> ListAsync(McpClient agent, RequestOptions session, CancellationToken cancellationToken)
	{
		var listed = await agent.CallToolAsync(ToolNames.DebugList, options: session, cancellationToken: cancellationToken);
		listed.IsError.ShouldNotBe(true, Text(listed));

		return listed.StructuredContent?.Deserialize<LiveAppSessionList>(ContractJson.Options)?.Sessions ?? [];
	}

	/// <summary>
	/// The client table's row for <paramref name="session"/>: the one that is new after it calls once more, among the
	/// rows <paramref name="before"/> lists -- the table names a row by an id of its own, not by the session's.
	/// </summary>
	private static async Task<string> RowOfAsync(
		OperatorBroker broker,
		McpClient agent,
		RequestOptions session,
		IReadOnlyList<ClientSummary> before,
		CancellationToken cancellationToken)
	{
		var calls = before.ToDictionary(client => client.Id, client => client.Calls);
		await ListAsync(agent, session, cancellationToken);

		var after = (await broker.GetAsync<ClientList>("/operator/clients", cancellationToken)).Clients;

		return after.Single(client => client.Named && calls.TryGetValue(client.Id, out var was) && client.Calls > was).Id;
	}

	private static async Task<T> PostAsync<T>(OperatorBroker broker, string path, object body, CancellationToken cancellationToken)
		where T : class
	{
		using var response = await broker.Client.PostAsJsonAsync(broker.Url(path), body, ContractJson.Options, cancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

		return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)).ShouldNotBeNull();
	}

	private static LiveAppSessionSummary Summary(ModelContextProtocol.Protocol.CallToolResult result) =>
		result.StructuredContent?.Deserialize<LiveAppSessionSummary>(ContractJson.Options) ?? throw new ShouldAssertException(Text(result));

	private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
		string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text));

	/// <summary>
	/// Puts back the machine's record of what a person debugged lately when a test is done with it: the broker a test
	/// starts is a real server reading this user's local application data, and a probe the suite attached to is not
	/// something the person debugged.
	/// </summary>
	private sealed class RecentTargetsKept : IDisposable
	{
		/// <summary>What the tests that start sessions from a window queue on, since each puts the one record back.</summary>
		public const string Key = "recent-debug-targets";

		private readonly string _path = Settings.RecentTargetsFile.PathFor();
		private readonly byte[]? _before;

		public RecentTargetsKept() => _before = File.Exists(_path) ? File.ReadAllBytes(_path) : null;

		public void Dispose()
		{
			try
			{
				if (_before is null) File.Delete(_path);
				else File.WriteAllBytes(_path, _before);
			}
			catch (IOException)
			{
				// Another process has it this moment; a stale recent entry is litter, not a failure.
			}
		}
	}

	/// <summary>The notices an answer's structured content carries.</summary>
	private static IReadOnlyList<string> Notices(ModelContextProtocol.Protocol.CallToolResult result) =>
		result.StructuredContent is { } content && content.TryGetProperty("notices", out var notices)
			? [.. notices.EnumerateArray().Select(notice => notice.GetString() ?? string.Empty)]
			: [];

	/// <summary>
	/// What a person does to a workspace through the real broker: its full status read on request, its projects
	/// included, without counting as use; a reload that answers with the new worker's row; and a close that takes the
	/// row away. None of it is a call an agent made, so the record holds only the agent's own.
	/// </summary>
	[Test]
	public async Task A_person_reads_reloads_and_closes_a_workspace()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var broker = await OperatorBroker.StartAsync(cancellationToken);
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(broker.Url("/")),
				AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
			}),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		(await agent.CallToolAsync(
			ToolNames.WorkspaceStatus,
			new Dictionary<string, object?> { ["workspace"] = fixture.SolutionPath },
			cancellationToken: cancellationToken)).IsError.ShouldNotBe(true);

		var before = (await broker.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken)).Workspaces.ShouldHaveSingleItem();
		var workspace = Uri.EscapeDataString(fixture.SolutionPath);

		var status = await broker.GetAsync<WorkspaceStatusReport>($"/operator/workspaces/status?workspace={workspace}", cancellationToken);
		status.Workspace.ShouldBe(fixture.SolutionPath);
		status.Projects.ShouldNotBeEmpty("a person reading why a load went as it did reads every project");
		status.Revision.ShouldBeGreaterThan(0);

		var after = (await broker.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken)).Workspaces.ShouldHaveSingleItem();
		after.LastUsedUtc.ShouldBe(before.LastUsedUtc, "reading a status is looking, not using");

		var body = new OperatorWorkspaceRequest { Workspace = fixture.SolutionPath };
		using (var reloaded = await broker.Client.PostAsJsonAsync(broker.Url("/operator/workspaces/reload"), body, ContractJson.Options, cancellationToken))
		{
			reloaded.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
			var row = (await reloaded.Content.ReadFromJsonAsync<OperatorWorkspace>(ContractJson.Options, cancellationToken))!;
			row.Summary.Workspace.ShouldBe(fixture.SolutionPath);
			row.Summary.ProcessId.ShouldNotBe(before.Summary.ProcessId, "a reload is a fresh worker");
		}

		using (var closed = await broker.Client.PostAsJsonAsync(broker.Url("/operator/workspaces/close"), body, ContractJson.Options, cancellationToken))
		{
			closed.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
			(await closed.Content.ReadFromJsonAsync<WorkspaceClosed>(ContractJson.Options, cancellationToken))!.Closed.ShouldBeTrue();
		}

		(await broker.GetAsync<OperatorWorkspaceList>("/operator/workspaces", cancellationToken)).Workspaces.ShouldBeEmpty();

		// By workspace, because every broker the suite starts writes to the one record folder.
		var activity = await broker.GetAsync<ActivityPage>($"/operator/activity?workspace={workspace}", cancellationToken);
		activity.Entries.Where(entry => entry.Kind == ActivityEntryKind.Call).Select(entry => entry.Tool).ShouldBe([ToolNames.WorkspaceStatus]);
	}

	/// <summary>The session's row of the memory report, once its host says it is serving the app.</summary>
	private static async Task<LiveAppMemory> SessionMemoryAsync(OperatorBroker broker, string sessionId, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + Ceiling;

		while (true)
		{
			var memory = await broker.GetAsync<MemoryReport>("/operator/memory", cancellationToken);
			var row = memory.LiveApps.ShouldHaveSingleItem();
			row.SessionId.ShouldBe(sessionId);

			if (row is { Host: not null, Target: not null }) return row;

			if (DateTime.UtcNow > deadline)
			{
				throw new ShouldAssertException($"The session's host and app were never both read: {JsonSerializer.Serialize(row, ContractJson.Options)}");
			}

			await Task.Delay(100, cancellationToken);
		}
	}

	/// <summary>Reads a log another process still holds open for writing.</summary>
	private static IEnumerable<string> Lines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);

		return reader.ReadToEnd().Split('\n');
	}

	/// <summary>
	/// An http broker whose operator token this test chose, with clients for both the authorised and
	/// the anonymous case.
	/// </summary>
	private sealed class OperatorBroker : IAsyncDisposable
	{
		private readonly RoseServerProcess _process;

		private OperatorBroker(RoseServerProcess process, int port)
		{
			_process = process;
			Port = port;

			Client = new HttpClient { Timeout = Ceiling };
			Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");

			Anonymous = new HttpClient { Timeout = Ceiling };
		}

		public int Port { get; }

		/// <summary>Carries the token, so every request is one an operator's tools would make.</summary>
		public HttpClient Client { get; }

		/// <summary>Carries nothing, which is what any other local process would be.</summary>
		public HttpClient Anonymous { get; }

		public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

		/// <summary>An operator view, read with the token.</summary>
		public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
			await Client.GetFromJsonAsync<T>(Url(path), ContractJson.Options, cancellationToken)
				?? throw new ShouldAssertException($"{path} answered nothing.");

		public static async Task<OperatorBroker> StartAsync(CancellationToken cancellationToken)
		{
			var port = RoseServerProcess.FreePort();
			var process = RoseServerProcess.StartWith(
				new Dictionary<string, string> { ["ROSEMCP_TOKEN"] = Token },
				"--transport",
				"http",
				"--port",
				port.ToString());

			var broker = new OperatorBroker(process, port);

			try
			{
				await broker.WaitUntilListeningAsync(cancellationToken);
				return broker;
			}
			catch
			{
				await broker.DisposeAsync();
				throw;
			}
		}

		/// <summary>
		/// Waits for the server to answer, bounded. An unbounded wait here turns a server that failed
		/// to bind into a wedged suite rather than a failing test.
		/// </summary>
		private async Task WaitUntilListeningAsync(CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + Ceiling;

			while (DateTime.UtcNow < deadline)
			{
				if (_process.HasExited) throw new InvalidOperationException("The broker exited before it answered.");

				try
				{
					using var answer = await Client.GetAsync(Url("/admin/workspaces"), cancellationToken);
					if (answer.IsSuccessStatusCode) return;
				}
				catch (HttpRequestException)
				{
					// Not up yet.
				}

				await Task.Delay(100, cancellationToken);
			}

			throw new InvalidOperationException($"The broker did not answer on port {Port} within {Ceiling.TotalSeconds:0}s.");
		}

		public ValueTask DisposeAsync()
		{
			Client.Dispose();
			Anonymous.Dispose();
			_process.Dispose();

			return ValueTask.CompletedTask;
		}
	}
}
