using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// A read from a degraded workspace says so, once, in one line. A read cannot report what its workspace
/// could not see, so without the notice an empty answer from a broken workspace reads exactly like a clean
/// bill of health; with it said more than once, or with every reason in full, it is the volume that gets
/// read past.
/// </summary>
public sealed class DegradedReadNoticeTests
{
	[Test]
	public void A_healthy_workspace_adds_no_notice() => DegradedNotice([]).ShouldBeNull();

	/// <summary>The why is the first reason's opening sentence; the remedy that follows it is status's to give.</summary>
	[Test]
	public void One_reason_is_said_by_its_opening_sentence_and_points_at_status()
	{
		var notice = DegradedNotice([AssemblyLoadReason([Fault("System.IO.Compression", "rose_find_references")])!]);

		notice.ShouldNotBeNull();
		notice.ShouldStartWith("This workspace is degraded", Case.Sensitive);
		notice.ShouldContain(
			"This worker could not load 1 assembly its own code needs, so the tools that reach that code fail on every "
				+ "call while the rest answer: System.IO.Compression (rose_find_references).",
			Case.Sensitive);
		notice.ShouldNotContain("A running process does not recover", Case.Sensitive);
		notice.ShouldNotContain("besides", Case.Sensitive);
		notice.ShouldEndWith("Ask rose_workspace_status for every reason and its fix.", Case.Sensitive);
		notice.ShouldNotContain(Environment.NewLine, Case.Sensitive);
	}

	/// <summary>The others are counted rather than said, however many there are.</summary>
	[Test]
	public void Further_reasons_are_counted_rather_than_repeated()
	{
		var notice = DegradedNotice(
		[
			"Project Core did not load successfully; its semantic results are unreliable.",
			"Project Web did not load successfully; its semantic results are unreliable.",
			"MSBuild reported 2 load failure(s); see loadDiagnostics.",
		]);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Project Core did not load successfully; its semantic results are unreliable. 2 more reasons besides.", Case.Sensitive);
		notice.ShouldNotContain("Project Web", Case.Sensitive);

		DegradedNotice(["Project Core did not load successfully.", "Project Web did not load successfully."])!
			.ShouldContain("1 more reason besides.", Case.Sensitive);
	}

	/// <summary>
	/// A reason quotes MSBuild, whose own message has full stops in it, so the opening sentence is the
	/// reason's rather than the quote's.
	/// </summary>
	[Test]
	public void A_full_stop_inside_a_quote_does_not_end_the_reason()
	{
		var reason = EvaluationReason(
			[new ProjectEvaluationFailure
			{
				Project = Path.Combine(Path.GetTempPath(), "Core", "Core.csproj"),
				Message = "The SDK 'Missing.Sdk' specified could not be found. Check the global.json.",
				NamesSdk = true,
			}],
			projectCount: 4,
			new HashSet<string>())!;

		DegradedNotice([reason])!.ShouldContain(
			"could not be evaluated by this worker's own MSBuild: \"The SDK 'Missing.Sdk' specified could not be found. "
				+ "Check the global.json.\" (1 project: Core). Ask rose_workspace_status",
			Case.Sensitive);
	}

	[Test]
	public void A_reason_with_no_full_stop_is_ended_with_one() =>
		DegradedNotice(["Restore failed"])!.ShouldContain("Restore failed. Ask rose_workspace_status", Case.Sensitive);

	/// <summary>Noted first, since it decides how the rest is read, and noted once however often it is noted.</summary>
	[Test]
	public void A_snapshot_leads_with_the_notice_and_takes_it_once()
	{
		using var workspace = new AdhocWorkspace();
		var snapshot = Snapshot(workspace, "Absorbed 1 external file change(s).");

		var noted = snapshot.Noting("degraded").Noting("degraded");

		noted.Notices.ShouldBe(["degraded", "Absorbed 1 external file change(s)."]);
		snapshot.Notices.ShouldBe(["Absorbed 1 external file change(s)."], "noting makes a new snapshot rather than changing this one");
	}

	[Test]
	public void Noting_nothing_is_the_same_snapshot()
	{
		using var workspace = new AdhocWorkspace();
		var snapshot = Snapshot(workspace);

		snapshot.Noting(null).ShouldBeSameAs(snapshot);
	}

	/// <summary>
	/// Every answer in a batch reads the same snapshot, so the notice is the batch's, said once, and on no
	/// entry -- a batch of twenty symbols must not say the workspace is degraded twenty-one times.
	/// </summary>
	[Test]
	public async Task A_batch_says_it_once_and_no_entry_repeats_it(CancellationToken cancellationToken)
	{
		using var workspace = new AdhocWorkspace();
		var notice = DegradedNotice(["Project Core did not load successfully; its semantic results are unreliable."]);
		var snapshot = Snapshot(workspace).Noting(notice);

		var batch = await ReadBatches.EachAsync(
			snapshot,
			["A.B", "A.C"],
			(requested, _) => Task.FromResult(new Answer([.. snapshot.Notices, $"{requested} is its own."])),
			_ => 0,
			(answer, shared) => answer with { Notices = ReadBatches.Own(answer.Notices, shared) },
			cancellationToken);

		batch.Notices.ShouldBe([notice!]);
		batch.Results.Select(entry => entry.Answer!.Notices).ShouldBe([["A.B is its own."], ["A.C is its own."]]);
	}

	private sealed record Answer(IReadOnlyList<string> Notices);

	private static WorkspaceSnapshot Snapshot(AdhocWorkspace workspace, params string[] notices) => new()
	{
		Solution = workspace.CurrentSolution,
		Revision = 1,
		Notices = notices,
	};

	private static AssemblyLoadFault Fault(string assembly, string tool) => new()
	{
		Assembly = assembly,
		Tool = tool,
		Message = $"Could not load file or assembly '{assembly}, Version=10.0.0.0'.",
		RuntimeDirectoryMissing = false,
	};
}
