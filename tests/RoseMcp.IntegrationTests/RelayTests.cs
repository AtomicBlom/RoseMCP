using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The relay, over two real processes. Nothing here can be asserted about either one alone: a stdio
/// session that relays looks exactly like one that does not until you ask which process is holding
/// the worker, and the directory it contributes is a fact the broker has no other way to learn.
/// </summary>
public sealed class RelayTests
{
	/// <summary>
	/// A stdio session relays to a tray that is already running, rather than starting workers of its
	/// own -- and the directory its client started it in is what decides the workspace.
	/// </summary>
	/// <remarks>
	/// Both halves in one test, because each is the other's control. That the tray holds the worker
	/// is what says the call was relayed rather than served locally; that a call naming no workspace
	/// at all resolves to the right solution is what says the relay contributed the one fact only it
	/// has. Either alone would pass against a relay that forwarded nothing useful.
	/// <para>
	/// The workspace argument is deliberately absent. An http broker serving the whole machine cannot
	/// know which repository a bare call means, and a stdio process cannot not know -- so a bare call
	/// answering correctly is the entire point of relaying rather than binding to the tray directly.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_relayed_session_is_served_by_the_tray_and_decided_by_its_own_directory()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var directory = Path.GetDirectoryName(fixture.SolutionPath)!;
		await using var relay = await RelayFixture.StartAsync(directory, cancellationToken);

		// No workspace argument: the session's own directory is the only thing that can decide this.
		using var answer = await relay.Session.CallToolAsync(
			ToolNames.WorkspaceStatus, "{}", cancellationToken);

		var status = Structured(answer);

		status.GetProperty("workspace").GetString().ShouldBe(fixture.SolutionPath);
		status.GetProperty("state").GetString().ShouldBe(nameof(WorkspaceState.Loaded));

		// And the tray is the process holding it, which is what says nothing was served locally.
		using var loaded = await relay.DescribeWorkspacesAsync(cancellationToken);

		var paths = loaded.RootElement.EnumerateArray()
			.Select(workspace => workspace.GetProperty("workspace").GetString())
			.ToList();

		paths.ShouldContain(fixture.SolutionPath);
	}

	/// <summary>
	/// The relay's failures carry their reason however many times they are asked, not only the first
	/// time.
	/// </summary>
	/// <remarks>
	/// Found by making it fail rather than by reading the hops, and the first call is exactly the one
	/// that hides it. After a tray restart call one said "The RoseMCP tray at ... is not answering",
	/// which is right, and every call after it said "An error occurred invoking 'rose_x'." -- the
	/// SDK's shrug, on the path where the caller most needs telling.
	/// <para>
	/// Two causes, both needed. The reconnect leaves the dead client in the field, so the next call
	/// goes to a disposed session and fails instantly as a <c>TaskCanceledException</c> -- which was
	/// not in the relay's idea of a transport failure, so it neither reconnected nor explained
	/// itself. And the relayed session applied no call-tool filter at all, because it declares no
	/// tools, so nothing turned that exception into a message.
	/// </para>
	/// <para>
	/// Three calls rather than two, because two would not have distinguished "the second is wrong"
	/// from "every one after the first is wrong", and the fix has to hold for both.
	/// </para>
	/// </remarks>
	[Test]
	public async Task A_relayed_call_says_why_it_failed_every_time_the_tray_is_gone()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var directory = Path.GetDirectoryName(fixture.SolutionPath)!;
		await using var relay = await RelayFixture.StartAsync(directory, cancellationToken);

		// A working call first, so what follows is a session that was relaying rather than one that
		// never connected.
		using (var working = await relay.Session.CallToolAsync(ToolNames.WorkspaceStatus, "{}", cancellationToken))
		{
			Structured(working);
		}

		await relay.StopBrokerAsync(cancellationToken);

		for (var attempt = 1; attempt <= 3; attempt++)
		{
			using var failed = await relay.Session.CallToolAsync(ToolNames.WorkspaceStatus, "{}", cancellationToken);

			var text = ErrorText(failed);

			failed.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue(
				$"call {attempt} should have failed with the tray gone, and said: {text}");

			// The reason is the discriminator, and it is the whole assertion. The SDK writes its own
			// preamble in front of every tool failure including the ones that do explain themselves,
			// so "An error occurred invoking" appearing says nothing; what separated the first call
			// from the rest was that only the first went on to say anything after it.
			text.ShouldContain("is not answering", Case.Sensitive);
		}
	}

	/// <summary>
	/// A worker's answer, through the tray's broker and the relay in front of it, reads in the text
	/// block a client hands the model with its plus sign as itself. The broker writes that text from
	/// the worker's structured content, and the relay passes the tray's result on; either one using a
	/// default encoder would spell the plus as an escape inside the string, which no client decodes.
	/// The worker's own text block never reaches here, so its registration is held by a test of its own.
	/// </summary>
	[Test]
	public async Task A_relayed_worker_answer_spells_its_source_as_written()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var directory = Path.GetDirectoryName(fixture.SolutionPath)!;
		await using var relay = await RelayFixture.StartAsync(directory, cancellationToken);

		using var answer = await relay.Session.CallToolAsync(
			ToolNames.SymbolInfo,
			"""{"symbols":["Core.Calculator.Add"],"includeSource":true}""",
			cancellationToken);

		Structured(answer);
		var text = ErrorText(answer);

		text.ShouldContain("left + right", Case.Sensitive);
		text.ShouldNotContain("\\" + "u002B", Case.Sensitive);
	}

	/// <summary>
	/// The http host names a write's paths relative only to a directory a call said it stands in. An
	/// agent talking to the broker directly says nothing, and the broker's own directory is nobody's,
	/// so the path comes back absolute -- even with the broker standing over the very file, where a
	/// path relative to it would look right and resolve, in the agent's own tools, against wherever the
	/// agent happens to be. The relayed session says where it stands, so its answer is relative to that.
	/// <para>
	/// Through the real http host, because what is under test is how that host configures its broker:
	/// a host that claimed its own directory as every caller's passes any test that builds the options
	/// itself.
	/// </para>
	/// </summary>
	[Test]
	public async Task The_http_host_names_paths_relative_only_to_a_directory_the_call_gave()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		await using var relay = await RelayFixture.StartAsync(fixture.Path(), cancellationToken, brokerDirectory: fixture.Path());

		await using var agent = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"http://127.0.0.1:{relay.Port}/") }),
			new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan },
			cancellationToken: cancellationToken);

		var direct = await agent.CallToolAsync(
			ToolNames.ReplaceDocComment,
			new Dictionary<string, object?>
			{
				["workspace"] = fixture.SolutionPath,
				["symbol"] = "Core.Calculator.Multiply",
				["comment"] = "Multiplies, over http.",
			},
			cancellationToken: cancellationToken);

		var written = JsonSerializer.SerializeToElement(direct.StructuredContent);
		written.ValueKind.ShouldBe(JsonValueKind.Object, $"the direct call failed: {JsonSerializer.Serialize(direct.Content)}");
		written.GetProperty("changedFiles")[0].GetProperty("filePath").GetString()
			.ShouldBe(fixture.Path("Simple", "Core", "Calculator.cs"), StringCompareShould.IgnoreCase);

		using var relayed = await relay.Session.CallToolAsync(
			ToolNames.ReplaceDocComment,
			$$"""
			{"workspace":{{JsonSerializer.Serialize(fixture.SolutionPath)}},"symbol":"Core.Calculator.Multiply","comment":"Multiplies, relayed."}
			""",
			cancellationToken);

		Structured(relayed).GetProperty("changedFiles")[0].GetProperty("filePath").GetString()
			.ShouldBe(Path.Combine("Simple", "Core", "Calculator.cs"));
	}

	/// <summary>
	/// What a deploy does to a session, over two real processes: the broker is killed and a new one
	/// started on the same port, and the relay finds it with no call to prompt it, sends no
	/// <c>list_changed</c> because the tools are the same, and says the restart on the next result --
	/// that one, and not the one after.
	/// </summary>
	/// <remarks>
	/// The same build restarted is the case every deploy that touched only the code behind the tools
	/// produces, and it is the one that must cost a session nothing. A changed tool list is the
	/// relay's decision about two surfaces, and the unit suite drives that against brokers whose tools
	/// it chooses; what only real processes can show is that the relay notices a killed broker by
	/// itself, over the watch, and that the broker counts it as connected again.
	/// </remarks>
	[Test]
	public async Task A_relay_finds_a_restarted_broker_by_itself_and_says_the_restart_once()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var directory = Path.GetDirectoryName(fixture.SolutionPath)!;
		await using var relay = await RelayFixture.StartAsync(directory, cancellationToken);

		var before = await relay.WaitForRelaysAsync(1, cancellationToken);
		before.RelayProtocol.ShouldBe(RelayProtocol.Version);

		using (var first = await relay.Session.CallToolAsync(ToolNames.WorkspaceList, "{}", cancellationToken))
		{
			NoticesOf(Structured(first)).ShouldBeEmpty("nothing has happened yet to say");
		}

		await relay.StopBrokerAsync(cancellationToken);
		await relay.RestartBrokerAsync(cancellationToken);

		// No call in between: the relay found the new broker over its watch, and is counted by it.
		var after = await relay.WaitForRelaysAsync(1, cancellationToken);
		after.Instance.ShouldNotBe(before.Instance, "the broker counting the relay is the one just started");

		using var next = await relay.Session.CallToolAsync(ToolNames.WorkspaceList, "{}", cancellationToken);
		var line = NoticesOf(Structured(next)).ShouldHaveSingleItem();

		line.ShouldStartWith($"RoseMCP restarted and is now build {after.Build.Describe()}", Case.Sensitive);
		line.ShouldContain("load again on the first call that needs them", Case.Sensitive);

		relay.Session.Notifications.ShouldNotContain(
			"notifications/tools/list_changed",
			"the new broker has the same tools, so the client has nothing to refresh");

		using var later = await relay.Session.CallToolAsync(ToolNames.WorkspaceList, "{}", cancellationToken);
		NoticesOf(Structured(later)).ShouldBeEmpty("the restart is said once");
	}

	/// <summary>
	/// A relay installed in an install's relay folder, with no tray running, hands the session to the
	/// install root's broker as a child rather than hosting one from its own assemblies -- and ends
	/// when that child does, which is what a deploy does to it.
	/// </summary>
	/// <remarks>
	/// The install is staged from this build's output, twice: once as the root and once as the relay
	/// folder under it, which is the arrangement a deploy produces. The relay's own assemblies are the
	/// same build here, so what shows the child served the call is the child process itself, found by
	/// where it runs from.
	/// </remarks>
	[Test]
	public async Task An_installed_relay_with_no_tray_relays_to_the_install_broker_and_ends_with_it()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var install = Path.Combine(Path.GetTempPath(), $"rosemcp-install-{Guid.NewGuid():N}");
		var output = Path.GetDirectoryName(RoseServerProcess.ExecutablePath())!;
		CopyDirectory(output, install);
		CopyDirectory(output, Path.Combine(install, RelayProtocol.Folder));

		try
		{
			var relayExecutable = Path.Combine(install, RelayProtocol.Folder, Path.GetFileName(RoseServerProcess.ExecutablePath()));
			using var session = RoseServerProcess.StartExecutable(relayExecutable, Path.GetDirectoryName(fixture.SolutionPath));
			await session.InitializeAsync(cancellationToken);

			using (var answer = await session.CallToolAsync(ToolNames.WorkspaceList, "{}", cancellationToken))
			{
				Structured(answer);
			}

			var broker = ProcessesRunningFrom(install).ShouldHaveSingleItem("the install root's broker is running as the session's child");

			using (broker)
			{
				broker.Kill(entireProcessTree: true);
			}

			(await session.WaitForExitAsync(TimeSpan.FromSeconds(30), cancellationToken)).ShouldBeTrue(
				"a relay whose broker has gone ends, so its client can start a session on the build installed now");
		}
		finally
		{
			foreach (var straggler in ProcessesRunningFrom(install))
			{
				using (straggler)
				{
					straggler.Kill(entireProcessTree: true);
				}
			}

			Directory.Delete(install, recursive: true);
		}
	}

	/// <summary>The servers running from directly under <paramref name="install"/>, which leaves its relay folder out.</summary>
	private static IReadOnlyList<Process> ProcessesRunningFrom(string install)
	{
		var name = Path.GetFileNameWithoutExtension(RoseServerProcess.ExecutablePath());
		var running = new List<Process>();

		foreach (var process in Process.GetProcessesByName(name))
		{
			string? path;
			try
			{
				path = process.MainModule?.FileName;
			}
			catch (Exception)
			{
				path = null;
			}

			var isTheInstallBroker = path is not null
				&& string.Equals(Path.GetDirectoryName(path), install, StringComparison.OrdinalIgnoreCase);

			if (isTheInstallBroker) running.Add(process);
			else process.Dispose();
		}

		return running;
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);

		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			var target = Path.Combine(destination, Path.GetRelativePath(source, file));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target);
		}
	}

	/// <summary>The notices a structured result carries, or none.</summary>
	private static IReadOnlyList<string> NoticesOf(JsonElement structured) =>
		structured.TryGetProperty("notices", out var notices)
			? [.. notices.EnumerateArray().Select(notice => notice.GetString()!)]
			: [];

	/// <summary>The structured half of a tool reply, or the error text when the call failed.</summary>
	internal static JsonElement Structured(JsonDocument reply)
	{
		var result = reply.RootElement.GetProperty("result");

		if (result.TryGetProperty("isError", out var failed) && failed.GetBoolean())
		{
			throw new ShouldAssertException($"the call failed: {ErrorText(reply)}");
		}

		return result.GetProperty("structuredContent");
	}

	/// <summary>
	/// What a failed call actually said, read off the wire rather than out of an exception. The text
	/// content is where a tool error's message lives, and the whole question in the relay's failure
	/// path is whether that message is the real one or a shrug.
	/// </summary>
	internal static string ErrorText(JsonDocument reply)
	{
		var result = reply.RootElement.GetProperty("result");
		if (!result.TryGetProperty("content", out var content)) return string.Empty;

		return string.Join(
			" ",
			content.EnumerateArray()
				.Where(entry => entry.TryGetProperty("text", out _))
				.Select(entry => entry.GetProperty("text").GetString()));
	}
}
