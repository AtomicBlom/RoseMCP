using System.Text.Json.Nodes;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// An acknowledgement end to end: the ids status hands out, acknowledged through the manager by a session named
/// the way a relay names one, and the worker's own read leaving the reasons out for that session and no other.
/// The halves are each held in the unit suite; this is what proves the broker sends the worker what it heard and
/// the worker hears it.
/// </summary>
public sealed class WorkspaceAcknowledgementTests
{
	[Test]
	public async Task A_sessions_acknowledgement_quiets_its_own_reads_and_no_other_sessions()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("Unrestored", "Unrestored.slnx");
		await using var manager = CreateManager();
		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		var status = await manager.StatusAsync(hints, cancellationToken);
		status.State.ShouldBe(WorkspaceState.Degraded);

		var ids = status.Health.Where(reason => reason.Effect != HealthEffect.Note).Select(reason => reason.Id).ToArray();
		ids.ShouldNotBeEmpty();

		var mine = Relayed();
		var theirs = Relayed();

		using (CallSession.Use(mine))
		{
			var acknowledged = await manager.AcknowledgeAsync(hints, ids, propose: false, note: null, cancellationToken);

			acknowledged.Workspace.ShouldBe(fixture.SolutionPath);
			acknowledged.Reasons.ShouldAllBe(reason => reason.Status == AcknowledgementOutcome.Acknowledged);

			(await ReadAsync(manager, fixture, cancellationToken)).ShouldNotContain(notice => Degraded(notice), "the session that acknowledged them hears nothing");
			(await manager.StatusAsync(hints, cancellationToken)).Health
				.Where(reason => ids.Contains(reason.Id))
				.ShouldAllBe(reason => reason.AcknowledgedUtc != null);
		}

		using (CallSession.Use(theirs))
		{
			(await ReadAsync(manager, fixture, cancellationToken)).ShouldContain(notice => Degraded(notice));
		}
	}

	private static bool Degraded(string notice) => notice.StartsWith("This workspace is degraded", StringComparison.Ordinal);

	/// <summary>A session named as the broker's call filter names a relayed one.</summary>
	private static string Relayed() =>
		CallSession.Of(null, new JsonObject { [CallSession.MetaKey] = CallSession.Mint() })
			?? throw new InvalidOperationException("A minted relay id was not taken for a session.");

	private static async Task<IReadOnlyList<string>> ReadAsync(WorkspaceManager manager, FixtureSolution fixture, CancellationToken cancellationToken)
	{
		var outline = await manager.CallAsync<ReadBatch<OutlineResult>>(
			WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath)),
			ToolNames.Outline,
			new Dictionary<string, object?> { ["filePath"] = fixture.Path("Unrestored", "App", "Mistake.cs") },
			retryIfWorkerDied: true,
			cancellationToken);

		return outline.Notices;
	}
}
