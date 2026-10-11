using System.Net;
using System.Text;
using System.Text.Json;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The inspector's side of the operator API, against a handler that answers without a server.
/// <para>
/// What is worth testing here is not that HTTP works: it is the things that are silent when they
/// are wrong. A missing bearer header looks like a tray that will not talk. A kinds filter joined
/// with commas returns everything and reads as a filter that does nothing. A 401 reported as a
/// generic failure sends the reader to the logs instead of to the tray. And a caller cancelling
/// must not surface as the tray timing out, because a pane closing mid-request would then put a
/// complaint about the tray on screen on its way out.
/// </para>
/// </summary>
public sealed class OperatorClientTests
{
	private static readonly Uri Tray = new("http://127.0.0.1:5077");

	[Test]
	public async Task The_token_travels_as_a_bearer_header()
	{
		using var handler = new FakeHandler(_ => Json(new LiveBreakpointList()));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.BreakpointsAsync("s1", TestContext.Current!.Execution.CancellationToken);

		(handler.Last!.Headers.Authorization?.Scheme).ShouldBe("Bearer");
		(handler.Last.Headers.Authorization?.Parameter).ShouldBe("tok-1");
	}

	/// <summary>A client with no token sends none, rather than sending an empty one.</summary>
	[Test]
	public async Task No_token_means_no_header()
	{
		using var handler = new FakeHandler(_ => Json(new LiveBreakpointList()));
		using var client = new OperatorClient(Tray, null, handler);

		client.HasToken.ShouldBeFalse();
		await client.BreakpointsAsync("s1", TestContext.Current!.Execution.CancellationToken);

		handler.Last!.Headers.Authorization.ShouldBeNull();
	}

	/// <summary>
	/// A broker that restarted has a new token, which reaches the app with the tray's next launch. Taking it on the
	/// client the windows already hold is what reconnects them: the next request carries it, and one with no token
	/// given sends none rather than the old one.
	/// </summary>
	[Test]
	public async Task A_new_token_is_presented_from_the_next_request()
	{
		using var handler = new FakeHandler(_ => Json(new LiveBreakpointList()));
		using var client = new OperatorClient(Tray, "old-run", handler);
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		client.Reauthorise("old-run").ShouldBeFalse("the same token is no change");
		client.Reauthorise("new-run").ShouldBeTrue();
		await client.BreakpointsAsync("s1", cancellationToken);
		(handler.Last!.Headers.Authorization?.Parameter).ShouldBe("new-run");

		client.Reauthorise(null).ShouldBeTrue();
		client.HasToken.ShouldBeFalse();
		await client.BreakpointsAsync("s1", cancellationToken);
		handler.Last!.Headers.Authorization.ShouldBeNull();
	}

	/// <summary>
	/// What a person does to a workspace names it by its path, which carries backslashes and a colon: in the body
	/// for reload and close, escaped in the query for the status read -- never as a route segment.
	/// </summary>
	[Test]
	public async Task Workspace_actions_name_the_solution_by_its_path()
	{
		const string Solution = @"D:\Drawboard\PDF\Drawboard.All.slnx";
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
		{
			"/operator/workspaces/status" => Json(new WorkspaceStatusReport
			{
				SolutionPath = Solution,
				State = WorkspaceState.Loaded,
				Revision = 3,
				Projects = [],
				LoadDiagnostics = [],
				DegradedReasons = [],
			}),
			"/operator/workspaces/close" => Json(new WorkspaceClosed { Workspace = Solution, Closed = true }),
			_ => Json(new OperatorWorkspace
			{
				Summary = new WorkspaceSummary
				{
					DisplayName = "Drawboard.All.slnx",
					Alive = true,
					ExitReason = string.Empty,
					State = WorkspaceState.Loading,
					StartedUtc = DateTime.UtcNow,
					Uptime = TimeSpan.Zero,
				},
				Folder = @"D:\Drawboard\PDF",
				LastUsedUtc = DateTime.UtcNow,
				IdleFor = TimeSpan.Zero,
				InUse = true,
			}),
		});
		using var client = new OperatorClient(Tray, "tok-1", handler);

		(await client.WorkspaceStatusAsync(Solution, cancellationToken)).Revision.ShouldBe(3);
		handler.Last!.Method.ShouldBe(HttpMethod.Get);
		handler.Last.RequestUri!.Query.ShouldBe($"?workspace={Uri.EscapeDataString(Solution)}&projects=true");

		(await client.ReloadWorkspaceAsync(Solution, cancellationToken)).InUse.ShouldBeTrue();
		handler.Last!.Method.ShouldBe(HttpMethod.Post);
		handler.Last.RequestUri!.AbsolutePath.ShouldBe("/operator/workspaces/reload");
		JsonSerializer.Deserialize<OperatorWorkspaceRequest>(handler.LastBody!, ContractJson.Options)!.Workspace.ShouldBe(Solution);

		(await client.CloseWorkspaceAsync(Solution, cancellationToken)).Closed.ShouldBeTrue();
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/workspaces/close");
		JsonSerializer.Deserialize<OperatorWorkspaceRequest>(handler.LastBody!, ContractJson.Options)!.Workspace.ShouldBe(Solution);
	}

	/// <summary>The person's side of a workspace's health is read and changed at the routes the broker maps for it.</summary>
	[Test]
	public async Task Health_decisions_go_to_the_health_routes()
	{
		const string Solution = @"D:\repo\A.slnx";
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var accepted = new AcceptedReasons { Workspace = Solution, Accepted = [] };
		var proposal = new ReasonProposal
		{
			Id = "p 1",
			Kind = "project-not-loaded",
			Line = "Core did not load.",
			AgentsRead = AgentsRead.RoseLine,
			ProposedUtc = DateTime.UtcNow,
		};

		using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
		{
			"/operator/health/proposals" => Json<IReadOnlyList<ReasonProposal>>([proposal]),
			var path when path.EndsWith("/reject", StringComparison.Ordinal) => Json(proposal),
			_ => Json(accepted),
		});
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.AcceptedAsync(Solution, cancellationToken);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe($"/operator/health/accepted?workspace={Uri.EscapeDataString(Solution)}");

		await client.AcceptAsync(new AcceptReasonRequest { Workspace = Solution, Kind = "k", Projects = ["Core"], AgentsRead = AgentsRead.Nothing }, cancellationToken);
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/health/accept");
		handler.LastBody!.ShouldContain("\"Nothing\"", Case.Sensitive, "a person's choice travels by name, which is how the route binds it");

		await client.StopAcceptingAsync(new StopAcceptingRequest { Workspace = Solution, Kind = "k", Projects = ["Core"] }, cancellationToken);
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/health/stop-accepting");

		(await client.ProposalsAsync(cancellationToken)).ShouldHaveSingleItem().Id.ShouldBe("p 1");

		await client.ConfirmProposalAsync("p 1", request: null, cancellationToken);
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/health/proposals/p%201/confirm");

		await client.RejectProposalAsync("p 1", cancellationToken);
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/health/proposals/p%201/reject");
	}

	/// <summary>
	/// A note goes to the notes route with its subject's kind by name, which is how the route binds it; and the notes
	/// are read there, waiting only when given a version and a wait.
	/// </summary>
	[Test]
	public async Task Notes_are_sent_and_read_at_the_notes_route()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		var note = new AgentNote
		{
			Id = "note-0123456789abcdef",
			ClientId = "client-01234567",
			ClientKey = "0123456789abcdef",
			Subject = new NoteSubject { Kind = NoteSubjectKind.ActivityCall, Readable = "rose_outline A", ActivityEntry = "0123456789abcdef" },
			Text = "Why?",
			PostedUtc = DateTimeOffset.UtcNow,
			Delivery = NoteDelivery.Waiting,
		};

		using var handler = new FakeHandler(request => request.Method == HttpMethod.Post
			? Json(note)
			: Json(new NoteList { Notes = [note], Version = "v2" }));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var sent = await client.SendNoteAsync(new NoteRequest { Client = note.ClientId, Subject = note.Subject, Text = note.Text }, cancellationToken);
		sent.Id.ShouldBe(note.Id);
		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/notes");
		handler.LastBody!.ShouldContain("\"ActivityCall\"", Case.Sensitive, "a subject's kind travels by name");

		(await client.NotesAsync(after: null, waitSeconds: 30, cancellationToken)).Version.ShouldBe("v2");
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/notes");

		await client.NotesAsync(after: "v1", waitSeconds: 30, cancellationToken);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/notes?after=v1&waitSeconds=30");
	}

	/// <summary>
	/// Hello reads the broker's build back whole, commit and checkout distance included: the window
	/// that shows them reads nothing else to learn which build it is talking to.
	/// </summary>
	[Test]
	public async Task Hello_reads_the_brokers_build_back()
	{
		var sent = new OperatorHello
		{
			Build = new BuildIdentity
			{
				Version = "1.3.0",
				Commit = new string('a', 40),
				Dirty = true,
				BuiltUtc = new DateTimeOffset(2026, 10, 11, 1, 2, 3, TimeSpan.Zero),
				Checkout = @"D:\src\RoseMCP",
			},
			Checkout = new CheckoutDistance
			{
				Checkout = @"D:\src\RoseMCP",
				Head = new CommitsPast { Ref = "HEAD", Count = 3 },
				OriginMain = new CommitsPast { Ref = "origin/main", Unknown = "no origin" },
				CheckedUtc = new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero),
			},
		};

		using var handler = new FakeHandler(_ => Json(sent));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var hello = await client.HelloAsync(TestContext.Current!.Execution.CancellationToken);

		handler.Last!.RequestUri!.AbsolutePath.ShouldBe("/operator/hello");
		hello.Build.ShouldBe(sent.Build);
		hello.Checkout.ShouldBe(sent.Checkout);
	}

	/// <summary>
	/// An inspector from another build than its broker says so, naming both commits, and says
	/// nothing where they match: a warning shown for every launch would be read by nobody.
	/// </summary>
	[Test]
	public void An_inspector_of_another_build_than_its_broker_says_so()
	{
		var inspector = new BuildIdentity { Version = "1.3.0", Commit = new string('a', 40), Dirty = false };
		var broker = inspector with { Commit = new string('b', 40) };

		InspectorText.AnotherBuild(inspector, inspector).ShouldBeNull();

		var warning = InspectorText.AnotherBuild(broker, inspector);

		warning.ShouldNotBeNull();
		warning!.ShouldStartWith("The broker is build 1.3.0 at bbbbbbb, where this one is 1.3.0 at aaaaaaa.");
	}

	/// <summary>
	/// Each kind is its own query parameter, because that is how ASP.NET binds a collection and a
	/// comma-joined list would arrive as one kind nothing matches -- a filter that silently returns
	/// nothing, which reads as a quiet target.
	/// </summary>
	[Test]
	public async Task The_events_query_repeats_each_kind()
	{
		using var handler = new FakeHandler(_ => Json(EmptyPage));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.EventsAsync(
			"s1", after: 12, kinds: ["BreakpointHit", "StepComplete"], limit: 50, waitSeconds: 30,
			TestContext.Current!.Execution.CancellationToken);

		var query = handler.Last!.RequestUri!.Query;
		query.ShouldContain("after=12", Case.Sensitive);
		query.ShouldContain("limit=50", Case.Sensitive);
		query.ShouldContain("waitSeconds=30", Case.Sensitive);
		query.ShouldContain("kinds=BreakpointHit", Case.Sensitive);
		query.ShouldContain("kinds=StepComplete", Case.Sensitive);
	}

	/// <summary>
	/// Each view is read from its own route, at once unless a version and a wait are both given: a wait sent with no
	/// version would be held for nothing, and a version sent with no wait is a read like any other.
	/// </summary>
	[Test]
	public async Task A_view_waits_only_when_given_a_version_and_a_wait()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
		{
			"/operator/clients" => Json(new ClientList { Version = "c1" }),
			"/operator/workspaces" => Json(new OperatorWorkspaceList { Version = "w1" }),
			"/operator/sessions" => Json(new OperatorSessionList { Version = "s1" }),
			_ => Json(new MemoryReport
			{
				SampledUtc = DateTime.UtcNow,
				Broker = new ProcessMemory { ProcessId = 1, WorkingSetBytes = 2, PrivateMemoryBytes = 3 },
			}),
		});
		using var client = new OperatorClient(Tray, "tok-1", handler);

		(await client.ClientsAsync(after: null, waitSeconds: 30, cancellationToken)).Version.ShouldBe("c1");
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/clients");

		await client.ClientsAsync(after: "c0", waitSeconds: 30, cancellationToken);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/clients?after=c0&waitSeconds=30");

		(await client.WorkspacesAsync(after: "w0", waitSeconds: 0, cancellationToken)).Version.ShouldBe("w1");
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/workspaces");

		await client.WorkspacesAsync(after: "w0", waitSeconds: 20, cancellationToken);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/workspaces?after=w0&waitSeconds=20");

		(await client.SessionListAsync(after: "s0", waitSeconds: 5, cancellationToken)).Version.ShouldBe("s1");
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/sessions?after=s0&waitSeconds=5");

		(await client.MemoryAsync(cancellationToken)).Broker.ProcessId.ShouldBe(1);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/memory");
	}

	/// <summary>
	/// The session list is an object carrying its version now, and the inspector's own read of it still answers the
	/// rows alone, so the inspector's list does not change with the shape.
	/// </summary>
	[Test]
	public async Task The_session_rows_are_read_out_of_the_versioned_list()
	{
		var row = new LiveAppSessionSummary
		{
			SessionId = "session-1",
			TargetDescription = "Probe",
			Architecture = TargetArchitecture.X64,
			State = LiveAppSessionState.Ready,
			StartedUtc = DateTime.UtcNow,
			Uptime = TimeSpan.FromSeconds(3),
			Execution = LiveExecutionState.Running,
			XamlStack = XamlStack.Unknown,
			XamlStackReason = "not asked",
			XamlProvider = LiveXamlProvider.None,
		};

		using var handler = new FakeHandler(_ => Json(new OperatorSessionList { Sessions = [row], Version = "v" }));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var sessions = await client.SessionsAsync(TestContext.Current!.Execution.CancellationToken);

		sessions.ShouldHaveSingleItem().SessionId.ShouldBe("session-1");
	}

	/// <summary>
	/// Every filter of an activity read reaches the query string escaped, and the cursor and the version go as their
	/// own arguments: text with an ampersand in it, unescaped, would end the filter early and add a stray argument.
	/// </summary>
	[Test]
	public async Task An_activity_read_carries_every_filter_escaped()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var handler = new FakeHandler(_ => Json(new ActivityPage { Bodies = false, Matched = 0, Retention = TimeSpan.FromDays(7) }));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var filter = new ActivityFilter
		{
			Client = "client-1",
			Workspace = @"D:\src\A.slnx",
			Session = "session-1",
			Tool = "rose_outline",
			Since = new DateTimeOffset(2026, 10, 11, 1, 2, 3, TimeSpan.Zero),
			Failures = true,
			Text = "cart & more",
			Limit = 20,
			Bodies = true,
		};

		await client.ActivityAsync(filter, before: "123-abc", after: "v1", waitSeconds: 25, cancellationToken);

		var query = handler.Last!.RequestUri!.Query;
		handler.Last.RequestUri.AbsolutePath.ShouldBe("/operator/activity");
		query.ShouldContain("client=client-1", Case.Sensitive);
		query.ShouldContain("workspace=D%3A%5Csrc%5CA.slnx", Case.Sensitive);
		query.ShouldContain("session=session-1", Case.Sensitive);
		query.ShouldContain("tool=rose_outline", Case.Sensitive);
		query.ShouldContain("since=2026-10-11T01%3A02%3A03.0000000%2B00%3A00", Case.Sensitive);
		query.ShouldContain("failures=true", Case.Sensitive);
		query.ShouldContain("text=cart%20%26%20more", Case.Sensitive);
		query.ShouldContain("limit=20", Case.Sensitive);
		query.ShouldContain("bodies=true", Case.Sensitive);
		query.ShouldContain("before=123-abc", Case.Sensitive);
		query.ShouldContain("after=v1&waitSeconds=25", Case.Sensitive);

		await client.ActivityAsync(new ActivityFilter(), before: null, after: null, waitSeconds: 0, cancellationToken);
		handler.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/activity");

		using var entries = new FakeHandler(_ => Json(new ActivityEntry
		{
			Id = "00ff",
			Kind = ActivityEntryKind.Call,
			Broker = "b",
			StartedUtc = DateTimeOffset.UtcNow,
		}));
		using var reader = new OperatorClient(Tray, "tok-1", entries);

		(await reader.ActivityEntryAsync("00ff", cancellationToken)).Id.ShouldBe("00ff");
		entries.Last!.RequestUri!.PathAndQuery.ShouldBe("/operator/activity/00ff");
	}

	/// <summary>
	/// A long poll gets its wait plus the margin. Budgeted at the quick allowance instead, every
	/// thirty-second poll would be abandoned ten seconds in and the tail would quietly miss events.
	/// </summary>
	[Test]
	public void A_long_poll_is_budgeted_past_the_wait_it_asked_for()
	{
		OperatorClient.PollBudget(30).ShouldBeGreaterThan(TimeSpan.FromSeconds(30));
		OperatorClient.PollBudget(30).ShouldBe(TimeSpan.FromSeconds(30) + OperatorClient.PollMargin);

		// A poll that waits for nothing still gets more than a quick call would need.
		OperatorClient.PollBudget(0).ShouldBe(OperatorClient.PollMargin);
	}

	/// <summary>An enum in a request body goes as its name, which is what the host binds.</summary>
	[Test]
	public async Task A_request_body_carries_words_rather_than_numbers()
	{
		using var handler = new FakeHandler(_ => Json(new LiveContinueResult { Continued = true }));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.StepAsync("s1", "over", TestContext.Current!.Execution.CancellationToken);

		handler.LastBody.ShouldBe("{\"mode\":\"over\"}");
	}

	/// <summary>A path's dots and brackets are escaped, since the grammar's separators are not the query's.</summary>
	[Test]
	public async Task A_value_path_is_escaped_into_the_query()
	{
		using var handler = new FakeHandler(_ => Json(new LiveValueExpansion
		{
			Execution = LiveExecutionState.StoppedAtBreakpoint,
			Path = "arg:0.Marks[1]",
			Total = 0,
			Truncated = false,
		}));

		using var client = new OperatorClient(Tray, "tok-1", handler);

		await client.ValueAsync("s1", "arg:0.Marks[1]", 0, null, TestContext.Current!.Execution.CancellationToken);

		handler.Last!.RequestUri!.Query.ShouldContain("path=arg%3A0.Marks%5B1%5D", Case.Sensitive);
	}

	[Test]
	public async Task A_refused_token_is_told_apart_and_says_what_to_do()
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
		using var client = new OperatorClient(Tray, "stale", handler);

		var failure = await Should.ThrowAsync<OperatorException>(
			async () => await client.SessionsAsync(TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Failure.ShouldBe(OperatorFailure.Unauthorized);
		failure.Status.ShouldBe(HttpStatusCode.Unauthorized);
		failure.Message.ShouldBe(InspectorText.TokenRefused);
	}

	[Test]
	public async Task A_session_that_is_gone_is_told_apart()
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Should.ThrowAsync<OperatorException>(
			async () => await client.SessionAsync("gone", TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Failure.ShouldBe(OperatorFailure.NotFound);
	}

	/// <summary>
	/// The host's own sentence survives the trip, which is the whole point of the operator API
	/// forwarding real messages rather than statuses.
	/// </summary>
	[Test]
	public async Task A_refusal_carries_the_hosts_own_words()
	{
		using var handler = new FakeHandler(_ => Json(
			new OperatorError { Message = "Frame 9 is past the end of this thread's stack.", Status = 400 },
			HttpStatusCode.BadRequest));

		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Should.ThrowAsync<OperatorException>(
			async () => await client.FrameVariablesAsync("s1", 9, null, TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Failure.ShouldBe(OperatorFailure.Refused);
		failure.Message.ShouldBe("Frame 9 is past the end of this thread's stack.");
	}

	/// <summary>Nothing listening is its own outcome, and the message says where it looked.</summary>
	[Test]
	public async Task Nothing_listening_reads_as_no_tray()
	{
		using var handler = new FakeHandler(_ => throw new HttpRequestException("refused"));
		using var client = new OperatorClient(Tray, "tok-1", handler);

		var failure = await Should.ThrowAsync<OperatorException>(
			async () => await client.SessionsAsync(TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Failure.ShouldBe(OperatorFailure.Unreachable);
		failure.Message.ShouldContain("127.0.0.1:5077", Case.Sensitive);
	}

	/// <summary>
	/// A caller giving up is not the tray failing. A pane closing mid-request must not put "the
	/// tray did not answer" on screen on its way out.
	/// </summary>
	[Test]
	public async Task A_caller_that_gives_up_is_not_a_timeout()
	{
		using var handler = new FakeHandler(async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		using var client = new OperatorClient(Tray, "tok-1", handler);
		using var abandoned = new CancellationTokenSource();

		var reading = client.SessionsAsync(abandoned.Token);
		await abandoned.CancelAsync();

		await Should.ThrowAsync<OperationCanceledException>(async () => await reading);
		reading.IsFaulted.ShouldBeFalse("a caller's own cancellation is a cancellation, not a fault");
	}

	private static readonly LiveDebugEventPage EmptyPage = new()
	{
		State = LiveAppSessionState.Ready,
		NextCursor = 0,
		OldestAvailable = 0,
		TotalObserved = 0,
	};

	private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
	{
		Content = new StringContent(
			JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json"),
	};

	/// <summary>
	/// A handler that answers from a function and remembers what it was asked, so a test can assert
	/// on the request as well as on what the client made of the reply.
	/// </summary>
	private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
		: HttpMessageHandler
	{
		public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
			: this((request, _) => Task.FromResult(answer(request)))
		{
		}

		/// <summary>The last request, kept because a URI composed wrongly is the quiet failure here.</summary>
		public HttpRequestMessage? Last { get; private set; }

		/// <summary>Its body as sent, for asserting how a request record serialises.</summary>
		public string? LastBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Last = request;
			LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

			return await answer(request, cancellationToken);
		}
	}
}
