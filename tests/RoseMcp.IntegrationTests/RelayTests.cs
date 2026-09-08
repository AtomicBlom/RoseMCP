using System.Text.Json;

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
	[Fact]
	public async Task A_relayed_session_is_served_by_the_tray_and_decided_by_its_own_directory()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");

		var directory = Path.GetDirectoryName(fixture.SolutionPath)!;
		await using var relay = await RelayFixture.StartAsync(directory, cancellationToken);

		// No workspace argument: the session's own directory is the only thing that can decide this.
		using var answer = await relay.Session.CallToolAsync(
			ToolNames.WorkspaceStatus, "{}", cancellationToken);

		var status = Structured(answer);

		Assert.Equal(fixture.SolutionPath, status.GetProperty("workspace").GetString());
		Assert.Equal(nameof(WorkspaceState.Loaded), status.GetProperty("state").GetString());

		// And the tray is the process holding it, which is what says nothing was served locally.
		using var loaded = await relay.DescribeWorkspacesAsync(cancellationToken);

		var paths = loaded.RootElement.EnumerateArray()
			.Select(workspace => workspace.GetProperty("workspace").GetString())
			.ToList();

		Assert.Contains(fixture.SolutionPath, paths);
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
	[Fact]
	public async Task A_relayed_call_says_why_it_failed_every_time_the_tray_is_gone()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
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

			Assert.True(
				failed.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(),
				$"call {attempt} should have failed with the tray gone, and said: {text}");

			// The reason is the discriminator, and it is the whole assertion. The SDK writes its own
			// preamble in front of every tool failure including the ones that do explain themselves,
			// so "An error occurred invoking" appearing says nothing; what separated the first call
			// from the rest was that only the first went on to say anything after it.
			Assert.Contains("is not answering", text, StringComparison.Ordinal);
		}
	}

	/// <summary>The structured half of a tool reply, or the error text when the call failed.</summary>
	internal static JsonElement Structured(JsonDocument reply)
	{
		var result = reply.RootElement.GetProperty("result");

		if (result.TryGetProperty("isError", out var failed) && failed.GetBoolean())
		{
			Assert.Fail($"the call failed: {ErrorText(reply)}");
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
