using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The person's side of a workspace's health, on the operator API. Accepting a reason changes what every
/// agent reads about it, and an agent can only propose one, so these routes are the only way an acceptance
/// is made and the token is the only thing standing between them and any local process.
/// <para>
/// Served by a real broker on a loopback port in this process, since the gate is middleware and the bodies
/// are bound by the framework: a handler called directly would test neither.
/// </para>
/// </summary>
public sealed class OperatorHealthRouteTests
{
	private static readonly string Shop = Path.Combine(Path.GetTempPath(), "rose-health-routes", "Shop.slnx");

	[Test]
	[Arguments("GET", "/operator/health/proposals")]
	[Arguments("POST", "/operator/health/accept")]
	[Arguments("POST", "/operator/health/stop-accepting")]
	[Arguments("POST", "/operator/health/proposals/proposal-00000000/confirm")]
	[Arguments("POST", "/operator/health/proposals/proposal-00000000/reject")]
	public async Task Every_health_route_refuses_a_request_without_the_operator_token(
		string method,
		string route,
		CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var broker = await Broker.StartAsync(home, cancellationToken);

		using var request = new HttpRequestMessage(new HttpMethod(method), route)
		{
			Content = method == "POST" ? JsonContent.Create(Accepting(), options: ContractJson.Options) : null,
		};

		using var response = await broker.Anonymous.SendAsync(request, cancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		File.Exists(Settings.WorkspaceHealthFile.PathFor(home.Path)).ShouldBeFalse("a refused request must change nothing");
	}

	[Test]
	public async Task A_person_accepts_a_reason_and_stops_accepting_it(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var broker = await Broker.StartAsync(home, cancellationToken);

		var accepted = await Read<AcceptedReasons>(
			await broker.Operator.PostAsJsonAsync("/operator/health/accept", Accepting(), ContractJson.Options, cancellationToken),
			cancellationToken);

		accepted.Workspace.ShouldBe(Shop);
		var reason = accepted.Accepted.ShouldHaveSingleItem();
		reason.AgentsRead.ShouldBe(AgentsRead.Note);
		reason.Note.ShouldBe("Expected: legacy tests nobody works in.");

		var listed = await broker.Operator.GetFromJsonAsync<AcceptedReasons>(
			$"/operator/health/accepted?workspace={Uri.EscapeDataString(Shop)}", ContractJson.Options, cancellationToken);
		listed!.Accepted.ShouldHaveSingleItem().Covering.ShouldBe("Legacy.Tests");

		var stopped = await Read<AcceptedReasons>(
			await broker.Operator.PostAsJsonAsync(
				"/operator/health/stop-accepting",
				new StopAcceptingRequest { Workspace = Shop, Kind = ReasonKinds.ProjectNotLoaded, Projects = ["Legacy.Tests"] },
				ContractJson.Options,
				cancellationToken),
			cancellationToken);

		stopped.Accepted.ShouldBeEmpty();
	}

	/// <summary>An agent proposes; the person's confirmation, on the route, is what makes it an acceptance.</summary>
	[Test]
	public async Task A_person_confirms_what_an_agent_proposed(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var broker = await Broker.StartAsync(home, cancellationToken);
		var health = broker.Services.GetRequiredService<WorkspaceHealth>();

		var proposal = health.Propose(Shop, Legacy(), "Legacy tests.");

		var waiting = await broker.Operator.GetFromJsonAsync<ReasonProposal[]>("/operator/health/proposals", ContractJson.Options, cancellationToken);
		waiting.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(proposal.Id);

		var confirmed = await Read<AcceptedReasons>(
			await broker.Operator.PostAsJsonAsync(
				$"/operator/health/proposals/{proposal.Id}/confirm",
				new ConfirmProposalRequest { AgentsRead = AgentsRead.Nothing },
				ContractJson.Options,
				cancellationToken),
			cancellationToken);

		confirmed.Accepted.ShouldHaveSingleItem().AgentsRead.ShouldBe(AgentsRead.Nothing);
		health.Proposals().ShouldBeEmpty();
	}

	/// <summary>The refusals a caller can act on are told apart, as every operator route tells them: a request to fix, a proposal that is gone.</summary>
	[Test]
	public async Task A_request_it_cannot_act_on_says_why(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var broker = await Broker.StartAsync(home, cancellationToken);

		using var gone = await broker.Operator.PostAsync("/operator/health/proposals/proposal-00000000/reject", null, cancellationToken);
		gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		(await gone.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken))!.Message.ShouldContain("proposal-00000000");

		using var relative = await broker.Operator.PostAsJsonAsync(
			"/operator/health/accept",
			Accepting() with { Workspace = "Shop.slnx" },
			ContractJson.Options,
			cancellationToken);
		relative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		using var noNote = await broker.Operator.PostAsJsonAsync(
			"/operator/health/accept",
			Accepting() with { Note = null },
			ContractJson.Options,
			cancellationToken);
		noNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>An acceptance naming neither projects nor subjects would cover nothing, so the route refuses it as a request to fix.</summary>
	[Test]
	public async Task An_acceptance_naming_nothing_it_covers_is_a_bad_request(CancellationToken cancellationToken)
	{
		using var home = new TemporaryLocalAppData();
		await using var broker = await Broker.StartAsync(home, cancellationToken);

		using var response = await broker.Operator.PostAsJsonAsync(
			"/operator/health/accept",
			Accepting() with { Kind = ReasonKinds.AnalyzerLoadFailed, Projects = [], Subjects = [] },
			ContractJson.Options,
			cancellationToken);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await response.Content.ReadFromJsonAsync<OperatorError>(ContractJson.Options, cancellationToken))!.Message.ShouldContain("subjects");
	}

	private static AcceptReasonRequest Accepting() => new()
	{
		Workspace = Shop,
		Kind = ReasonKinds.ProjectNotLoaded,
		Projects = ["Legacy.Tests"],
		AgentsRead = AgentsRead.Note,
		Note = "Expected: legacy tests nobody works in.",
	};

	private static HealthReason Legacy() => new()
	{
		Id = $"{ReasonKinds.ProjectNotLoaded}:Legacy.Tests",
		Kind = ReasonKinds.ProjectNotLoaded,
		Effect = HealthEffect.MayAffectSomeProjects,
		Projects = ["Legacy.Tests"],
		Line = "Project Legacy.Tests did not load successfully; its semantic results are unreliable.",
	};

	private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		using (response)
		{
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));

			return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken))!;
		}
	}

	/// <summary>A broker serving the operator API on a loopback port of its own, with a client holding the token and one without.</summary>
	private sealed class Broker : IAsyncDisposable
	{
		private readonly WebApplication _app;

		private Broker(WebApplication app, OperatorToken token)
		{
			_app = app;

			var address = new Uri(app.Urls.First());
			Anonymous = new HttpClient { BaseAddress = address };
			Operator = new HttpClient { BaseAddress = address };
			Operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
		}

		public HttpClient Anonymous { get; }

		public HttpClient Operator { get; }

		public IServiceProvider Services => _app.Services;

		public static async Task<Broker> StartAsync(TemporaryLocalAppData home, CancellationToken cancellationToken)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			builder.Services.AddRoseMcpBroker(broker => broker.LocalAppData = home.Path);

			var app = builder.Build();
			var token = OperatorToken.Mint();
			app.MapRoseOperatorApi(token);

			await app.StartAsync(cancellationToken);

			return new Broker(app, token);
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
