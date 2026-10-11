using System.Net;
using System.Text;
using System.Text.Json;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// The Inspector's start page and the header of a session a person started, as a person reads them: which running
/// apps are offered and what each one's button does, what the launch form takes, what a recent target says and does
/// again, who a session can be shared with, and who is in a session.
/// </summary>
public sealed class StartPageTests
{
	private static RunningApp App(
		int pid = 22104,
		string name = "RoseProbe",
		string? title = "Rose Probe",
		XamlStack stack = XamlStack.WinUi,
		string? sessionId = null,
		LiveAppStarter? startedBy = null,
		bool stopped = false,
		string? path = @"D:\Probe\bin\RoseProbe.exe") => new()
		{
			ProcessId = pid,
			Name = name,
			WindowTitle = title,
			ExecutablePath = path,
			XamlStack = stack,
			Architecture = TargetArchitecture.X64,
			HasWindow = title is not null,
			SessionId = sessionId,
			SessionState = sessionId is null ? null : LiveAppSessionState.Ready,
			SessionStartedBy = startedBy,
			SessionStopped = stopped,
		};

	/// <summary>An app nobody is debugging is attached to; its row names it by its window, its framework and its architecture.</summary>
	[Test]
	public void An_app_nobody_debugs_is_attached_to()
	{
		var row = new RunningAppRow(App());

		row.DisplayName.ShouldBe("Rose Probe");
		row.Kind.ShouldBe("WinUI 3 · x64");
		row.Status.ShouldBe("Not being debugged");
		row.Action.ShouldBe("Attach");
		row.IsDebugged.ShouldBeFalse();
		row.Pid.ShouldBe("22104");
	}

	/// <summary>
	/// An app being debugged opens the session that is debugging it, since a process has one debugger, and its row
	/// says by whom and whether it is stopped.
	/// </summary>
	[Test]
	public void An_app_being_debugged_opens_its_session()
	{
		var row = new RunningAppRow(App(sessionId: "session-1", startedBy: LiveAppStarter.Agent, stopped: true));

		row.Action.ShouldBe("Open");
		row.SessionId.ShouldBe("session-1");
		row.Status.ShouldBe("Being debugged by an agent · stopped");

		row.Update(App(sessionId: "session-1", startedBy: LiveAppStarter.Person));
		row.Status.ShouldBe("Being debugged by you");

		row.Update(App(title: null, stack: XamlStack.Unknown));
		row.DisplayName.ShouldBe("RoseProbe");
		row.Kind.ShouldBe(".NET · x64 · no window");
		row.Action.ShouldBe("Attach");
	}

	/// <summary>The search matches a name, a title, a path, a package or a process id, ignoring case; nothing typed matches all.</summary>
	[Test]
	public void The_search_matches_what_a_row_shows_and_a_little_more()
	{
		var app = App() with { AppUserModelId = "RoseProbe_8wekyb3d8bbwe!App" };

		RunningAppRow.Matches(app, null).ShouldBeTrue();
		RunningAppRow.Matches(app, "rose probe").ShouldBeTrue();
		RunningAppRow.Matches(app, @"probe\bin").ShouldBeTrue();
		RunningAppRow.Matches(app, "8wekyb3d8bbwe").ShouldBeTrue();
		RunningAppRow.Matches(app, "2210").ShouldBeTrue();
		RunningAppRow.Matches(app, "drawboard").ShouldBeFalse();
	}

	/// <summary>
	/// The launch form takes a program by its full path, quotes around it dropped, with its arguments as typed; a path
	/// that is not full or not an executable is said to be so before anything is asked of the broker.
	/// </summary>
	[Test]
	public void The_launch_form_takes_a_full_path_to_an_executable()
	{
		var request = StartPage.Program(@"""D:\Tools\InkLab.exe""", "  --sample strokes.json ", out var problem).ShouldNotBeNull();
		problem.ShouldBeNull();
		request.ExecutablePath.ShouldBe(@"D:\Tools\InkLab.exe");
		request.Arguments.ShouldBe("--sample strokes.json");
		request.AppUserModelId.ShouldBeNull();

		StartPage.Program(@"D:\Tools\InkLab.exe", "  ", out _).ShouldNotBeNull().Arguments.ShouldBeNull();

		StartPage.Program(@"bin\InkLab.exe", null, out problem).ShouldBeNull();
		problem.ShouldNotBeNull().ShouldContain("is not a full path");

		StartPage.Program(@"D:\Tools\InkLab.dll", null, out problem).ShouldBeNull();
		problem.ShouldNotBeNull().ShouldContain("is not an .exe");

		StartPage.Program("", null, out problem).ShouldBeNull();
		problem.ShouldNotBeNull().ShouldContain("Name the program");
	}

	/// <summary>A packaged app is launched by its id, named as the installed list names it; its row marks development mode.</summary>
	[Test]
	public void A_packaged_app_is_launched_by_its_id()
	{
		var app = new InstalledApp
		{
			AppUserModelId = "Drawboard.PDF_gqbn7fs4pywxm!App",
			DisplayName = "Drawboard PDF",
			PackageFamilyName = "Drawboard.PDF_gqbn7fs4pywxm",
			DevelopmentMode = true,
		};

		StartPage.Package(app).ShouldBe(new LaunchRequest { AppUserModelId = "Drawboard.PDF_gqbn7fs4pywxm!App", DisplayName = "Drawboard PDF" });
		StartPage.Describe(app).ShouldBe("Drawboard PDF · Drawboard.PDF_gqbn7fs4pywxm · development");
	}

	/// <summary>A recent target's chip says what it was, what was done, and when, in days as a person counts them.</summary>
	[Test]
	public void A_recent_chip_says_what_was_done_and_when()
	{
		var now = DateTime.UtcNow;
		var launched = new RecentTarget { Kind = LiveAppTargetKind.LaunchExecutable, DisplayName = "InkLab.exe", LastUsedUtc = now };
		var attached = launched with { Kind = LiveAppTargetKind.AttachProcess, DisplayName = "RoseProbe" };

		StartPage.Chip(launched, now).ShouldBe("InkLab.exe · launched · today");
		StartPage.Chip(attached, now).ShouldBe("RoseProbe · attached · today");

		StartPage.When(now.AddDays(-1), now).ShouldBe("yesterday");
		StartPage.When(now.AddDays(-3), now).ShouldBe("3 days ago");
		StartPage.When(now.AddDays(-30), now).ShouldNotContain("ago");
	}

	/// <summary>
	/// A recent launch is launched again as it was; a recent attach attaches to the one running copy of its program,
	/// and says so rather than guessing where there is none or several.
	/// </summary>
	[Test]
	public void A_recent_target_is_done_again_as_it_was_done()
	{
		var program = new RecentTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			DisplayName = "InkLab.exe",
			ExecutablePath = @"D:\Tools\InkLab.exe",
			Arguments = "--sample strokes.json",
			LastUsedUtc = DateTime.UtcNow,
		};

		var launch = StartPage.Again(program, [], out _).ShouldNotBeNull().Launch.ShouldNotBeNull();
		launch.ExecutablePath.ShouldBe(@"D:\Tools\InkLab.exe");
		launch.Arguments.ShouldBe("--sample strokes.json");

		var package = program with { Kind = LiveAppTargetKind.LaunchUwp, DisplayName = "Drawboard PDF", AppUserModelId = "Drawboard.PDF_x!App", ExecutablePath = null };
		StartPage.Again(package, [], out _).ShouldNotBeNull().Launch.ShouldNotBeNull().AppUserModelId.ShouldBe("Drawboard.PDF_x!App");

		var attach = program with { Kind = LiveAppTargetKind.AttachProcess, DisplayName = "RoseProbe", ExecutablePath = @"d:\probe\bin\roseprobe.exe", Arguments = null };

		StartPage.Again(attach, [App()], out _).ShouldNotBeNull().ProcessId.ShouldBe(22104);

		StartPage.Again(attach, [], out var none).ShouldBeNull();
		none.ShouldNotBeNull().ShouldContain("is not running");

		StartPage.Again(attach, [App(), App(pid: 22105)], out var several).ShouldBeNull();
		several.ShouldNotBeNull().ShouldContain("2 copies");
	}

	/// <summary>Only an agent session behind a relay can be shared with; the unnamed client could be any client without one.</summary>
	[Test]
	public void Only_a_session_behind_a_relay_can_be_shared_with()
	{
		ClientSummary Client(string id, bool named, string? label) => new()
		{
			Id = id,
			Named = named,
			ConnectedUtc = DateTimeOffset.UtcNow,
			LastCallUtc = DateTimeOffset.UtcNow,
			Label = label,
		};

		var choices = StartPage.ShareChoices([Client("client-1", named: false, null), Client("client-2", named: true, "PDF fixes")]);

		choices.ShouldHaveSingleItem().Id.ShouldBe("client-2");
		choices[0].Name.ShouldContain("PDF fixes");
	}

	/// <summary>
	/// Who is in a session, as its header says it: an agent's; yours alone; yours and the agent session you shared it
	/// with, named, and said not to be connected while its relay is away.
	/// </summary>
	[Test]
	public void A_session_says_who_is_in_it()
	{
		var agents = SessionRowTests.Session();
		var mine = agents with { StartedBy = LiveAppStarter.Person };
		var shared = mine with { SharedWith = new LiveAppShare { ClientId = "client-2", Name = "PDF fixes", Connected = true } };

		SessionRow.DescribeOwners(agents).ShouldBe("Started by an agent");
		SessionRow.DescribeOwners(mine).ShouldBe("Started by you · not shared with an agent");
		SessionRow.DescribeOwners(shared).ShouldBe("Started by you · shared with PDF fixes");
		SessionRow.DescribeOwners(shared with { SharedWith = shared.SharedWith! with { Connected = false } })
			.ShouldBe("Started by you · shared with PDF fixes (not connected)");

		var row = new SessionRow(shared);
		row.IsPersons.ShouldBeTrue();
		row.IsShared.ShouldBeTrue();
		row.SharedWithClientId.ShouldBe("client-2");

		row.Update(mine);
		row.IsShared.ShouldBeFalse();
		new SessionRow(agents).IsPersons.ShouldBeFalse();
	}

	/// <summary>
	/// An attach and a launch post what the start page asked for, and a conflict -- the target being debugged already,
	/// or the host not reaching it -- comes back as a refusal in the broker's own words.
	/// </summary>
	[Test]
	public async Task A_start_posts_what_was_asked_and_a_conflict_is_a_refusal()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		string? path = null;
		string? body = null;

		using var handler = new Answering(async request =>
		{
			path = request.RequestUri!.PathAndQuery;
			body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

			var mine = SessionRowTests.Session() with { StartedBy = LiveAppStarter.Person };

			return request.RequestUri.AbsolutePath switch
			{
				"/operator/debug/launch" => Json(new OperatorError { Status = 409, Message = "pid 4 is already being debugged." }, HttpStatusCode.Conflict),
				"/operator/debug/apps" => Json(new RunningAppList()),
				"/operator/sessions/session-1/share" => Json(mine),
				_ => Json(new DebugStart { Session = mine }),
			};
		});
		using var client = new OperatorClient(new Uri("http://127.0.0.1:5077"), "tok", handler);

		var attached = await client.AttachAsync(22104, cancellationToken);
		path.ShouldBe("/operator/debug/attach");
		body.ShouldNotBeNull().ShouldContain("\"processId\":22104");
		attached.Session.StartedBy.ShouldBe(LiveAppStarter.Person);

		var refused = await Should.ThrowAsync<OperatorException>(
			() => client.LaunchAsync(new LaunchRequest { ExecutablePath = @"D:\Tools\InkLab.exe" }, cancellationToken));
		refused.Failure.ShouldBe(OperatorFailure.Refused);
		refused.Message.ShouldBe("pid 4 is already being debugged.");

		await client.RunningAppsAsync(every: true, cancellationToken);
		path.ShouldBe("/operator/debug/apps?every=true");

		await client.UnshareAsync("session-1", cancellationToken);
		path.ShouldBe("/operator/sessions/session-1/share");
	}

	private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
	{
		Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json"),
	};

	/// <summary>A handler that answers from a function, without a server.</summary>
	private sealed class Answering(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
	}
}
