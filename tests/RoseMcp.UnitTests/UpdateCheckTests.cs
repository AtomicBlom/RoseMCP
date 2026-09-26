using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using RoseMcp.Ui.Core.Updates;

namespace RoseMcp.UnitTests;

/// <summary>
/// The update check, against a handler that answers without GitHub.
/// <para>
/// What is worth testing is not that HTTP works. It is that nothing GitHub or the network does
/// reaches the person running the tray: a rate limit, a hotspot that intercepts the request, a tag
/// that is not a version, and an answer with no tag at all are each a way for a background poll to
/// take down a window that was doing something useful. And the version comparison has two silent
/// failures of its own -- offering an upgrade that no upgrade can clear, or never offering one.
/// </para>
/// </summary>
public sealed class UpdateCheckTests
{
	private static readonly Uri Endpoint = new("https://api.github.com/repos/AtomicBlom/RoseMCP/releases/latest");

	[Test]
	public async Task Reports_an_update_when_the_release_is_newer()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"v0.4.0","html_url":"https://example.invalid/0.4.0"}"""));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeTrue();
		status.LatestVersion.ShouldBe("0.4.0");
		status.ReleaseUrl.ShouldBe("https://example.invalid/0.4.0");
		status.Problem.ShouldBeNull();
	}

	[Test]
	public async Task Reports_nothing_when_the_running_build_is_current()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"v0.4.0"}"""));
		using var check = new UpdateCheck("0.4.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeFalse();
		status.Problem.ShouldBeNull();
	}

	/// <summary>A build ahead of the latest release is not offered a downgrade.</summary>
	[Test]
	public async Task Reports_nothing_when_the_running_build_is_ahead()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"v0.3.0"}"""));
		using var check = new UpdateCheck("0.4.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeFalse();
	}

	/// <summary>GitHub answers 403 to a request with no User-Agent, and that reads as a permission problem.</summary>
	[Test]
	public async Task Identifies_itself_because_github_refuses_a_request_without_it()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"v0.4.0"}"""));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		handler.Last!.Headers.UserAgent.ToString().ShouldContain("RoseMCP", Case.Sensitive);
	}

	/// <summary>
	/// A '+' is not a token character, so the build metadata MinVer stamps would throw on the way into
	/// the header -- at construction, nowhere near anything that looks like a header problem.
	/// </summary>
	[Test]
	public async Task An_informational_version_does_not_break_the_user_agent()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"v0.4.0"}"""));
		using var check = new UpdateCheck("1.1.1-alpha.0.9+1a2b3c4", Endpoint, handler);

		await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		handler.Last!.Headers.UserAgent.ToString().ShouldContain("1.1.1-alpha.0.9", Case.Sensitive);
	}

	/// <summary>
	/// A 304 costs no quota against the anonymous sixty-an-hour limit, which is per address and shared
	/// with everything else on the machine -- so the answer has to survive one.
	/// </summary>
	[Test]
	public async Task Sends_the_previous_etag_and_keeps_its_answer_on_a_304()
	{
		var calls = 0;
		using var handler = new FakeHandler(_ =>
		{
			calls++;
			if (calls > 1) return new HttpResponseMessage(HttpStatusCode.NotModified);

			var first = Json("""{"tag_name":"v0.4.0","html_url":"https://example.invalid/0.4.0"}""");
			first.Headers.ETag = new EntityTagHeaderValue("\"abc\"");

			return first;
		});

		using var check = new UpdateCheck("0.3.0", Endpoint, handler);
		await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);
		var second = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		handler.Last!.Headers.IfNoneMatch.ToString().ShouldBe("\"abc\"");
		second.IsUpdateAvailable.ShouldBeTrue();
		second.LatestVersion.ShouldBe("0.4.0");
	}

	[Test]
	[Arguments(HttpStatusCode.Forbidden)]
	[Arguments(HttpStatusCode.TooManyRequests)]
	public async Task Says_so_when_rate_limited_rather_than_failing(HttpStatusCode code)
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(code));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeFalse();
		status.Problem.ShouldNotBeNull();
		status.Problem!.ShouldContain("rate limiting", Case.Sensitive);
	}

	/// <summary>
	/// A tray that crashed because a hotspot intercepted a request would be a worse bug than the one
	/// this feature fixes.
	/// </summary>
	[Test]
	public async Task A_network_failure_is_carried_rather_than_thrown()
	{
		using var handler = new FakeHandler(_ => throw new HttpRequestException("no such host"));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeFalse();
		status.Problem!.ShouldContain("Could not reach GitHub", Case.Sensitive);
	}

	[Test]
	public async Task A_server_error_is_reported_with_its_code()
	{
		using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.Problem!.ShouldContain("503", Case.Sensitive);
	}

	/// <summary>Assuming newer would put a notice on screen that no upgrade could ever clear.</summary>
	[Test]
	public async Task A_tag_that_is_not_a_version_is_reported_rather_than_assumed_newer()
	{
		using var handler = new FakeHandler(_ => Json("""{"tag_name":"nightly"}"""));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		var status = await check.CheckAsync(TestContext.Current!.Execution.CancellationToken);

		status.IsUpdateAvailable.ShouldBeFalse();
		status.Problem!.ShouldContain("nightly", Case.Sensitive);
	}

	[Test]
	public async Task An_answer_with_no_tag_is_reported()
	{
		using var handler = new FakeHandler(_ => Json("{}"));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		(await check.CheckAsync(TestContext.Current!.Execution.CancellationToken)).Problem.ShouldNotBeNull();
	}

	[Test]
	public async Task Malformed_json_does_not_escape()
	{
		using var handler = new FakeHandler(_ => Json("this is not json"));
		using var check = new UpdateCheck("0.3.0", Endpoint, handler);

		(await check.CheckAsync(TestContext.Current!.Execution.CancellationToken)).Problem.ShouldNotBeNull();
	}

	/// <summary>
	/// The build decides, and the environment has the last word so the thing can be tried without
	/// cutting a release. This test assembly is stamped like every build that is not one.
	/// </summary>
	[Test]
	public void The_build_decides_and_the_environment_overrides_it()
	{
		var assembly = typeof(UpdateCheckTests).Assembly;

		UpdateCheckPolicy.EnabledFor(assembly, null).ShouldBeFalse();
		UpdateCheckPolicy.EnabledFor(assembly, "1").ShouldBeTrue();
		UpdateCheckPolicy.EnabledFor(assembly, "true").ShouldBeTrue();
		UpdateCheckPolicy.EnabledFor(assembly, "0").ShouldBeFalse();

		// Something neither on nor off leaves the build's own answer alone rather than guessing.
		UpdateCheckPolicy.EnabledFor(assembly, "perhaps").ShouldBeFalse();
	}

	/// <summary>
	/// A release build against one the workflow never stamped.
	/// <para>
	/// A dynamic assembly, because every assembly in this repository carries the attribute already --
	/// set to false, since nothing here is a release -- so a stand-in type in this project cannot ask
	/// the question either way.
	/// </para>
	/// </summary>
	[Test]
	public void A_stamped_assembly_reads_as_enabled()
	{
		var stamped = Stamped("true");
		var unstamped = Stamped(null);

		UpdateCheckPolicy.EnabledFor(stamped, null).ShouldBeTrue();
		UpdateCheckPolicy.EnabledFor(unstamped, null).ShouldBeFalse();

		// And a build with it on can still be told to be quiet.
		UpdateCheckPolicy.EnabledFor(stamped, "off").ShouldBeFalse();
	}

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json"),
	};

	/// <summary>
	/// An assembly carrying the metadata a release build would, or none at all when
	/// <paramref name="value"/> is null.
	/// </summary>
	private static Assembly Stamped(string? value)
	{
		var assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"RoseMcp.UpdateCheckProbe{Guid.NewGuid():N}"),
			AssemblyBuilderAccess.Run);

		if (value is not null)
		{
			var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;

			assembly.SetCustomAttribute(
				new CustomAttributeBuilder(constructor, [UpdateCheckPolicy.MetadataKey, value]));
		}

		return assembly;
	}

	/// <summary>
	/// A handler that answers from a function and remembers what it was asked, so a test can assert on
	/// the request as well as on what the check made of the reply. A missing header is the quiet
	/// failure here, and only the request shows it.
	/// </summary>
	private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
	{
		public HttpRequestMessage? Last { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Last = request;

			return Task.FromResult(answer(request));
		}
	}
}
