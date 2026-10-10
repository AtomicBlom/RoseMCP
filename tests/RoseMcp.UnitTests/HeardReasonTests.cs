using System.Text.Json;
using System.Text.Json.Nodes;

using RoseMcp.Contracts;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// A read's degraded notice is written for whoever is asking: a reason the person accepted saying nothing,
/// or one this session acknowledged, is left out, and one accepted with a note is said in the person's
/// words. Keyed on kind and project, never blanket, because the failure the other way is a reason nobody
/// accepted going unsaid -- which is the confident clean answer the notice exists to prevent.
/// </summary>
public sealed class HeardReasonTests
{
	private static readonly WorkspaceReason Legacy = WorkspaceReason.Of(
		ReasonKinds.ProjectNotLoaded,
		HealthEffect.MayAffectSomeProjects,
		"Project Legacy.Tests did not load successfully; its semantic results are unreliable.",
		["Legacy.Tests"]);

	private static readonly WorkspaceReason Failures = WorkspaceReason.Of(
		ReasonKinds.MsBuildLoadFailures,
		HealthEffect.MayAffectSomeProjects,
		"MSBuild reported 214 load failure(s); see loadDiagnostics.",
		["Legacy.Tests", "Ink.Tests"]);

	[Test]
	public void A_reason_nobody_accepted_is_said_as_it_always_was() =>
		DegradedNotice([Legacy], []).ShouldBe(DegradedNotice([Legacy.Text]));

	[Test]
	public void A_reason_accepted_as_nothing_leaves_a_read_saying_nothing() =>
		DegradedNotice([Legacy], [Heard(ReasonKinds.ProjectNotLoaded, "Legacy.Tests", AgentsRead.Nothing)]).ShouldBeNull();

	[Test]
	public void A_reason_accepted_with_a_note_is_said_in_the_persons_words()
	{
		var notice = DegradedNotice(
			[Legacy],
			[Heard(ReasonKinds.ProjectNotLoaded, "legacy.tests", AgentsRead.Note, "Expected: legacy tests nobody works in.")]);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Expected: legacy tests nobody works in.", Case.Sensitive);
		notice.ShouldNotContain("did not load", Case.Sensitive);
	}

	/// <summary>A note that is empty says nothing a person meant, so Rose's line stands in for it rather than an empty quote.</summary>
	[Test]
	public void An_empty_note_falls_back_to_roses_line() =>
		DegradedNotice([Legacy], [Heard(ReasonKinds.ProjectNotLoaded, "Legacy.Tests", AgentsRead.Note, " ")])!
			.ShouldContain("Project Legacy.Tests did not load successfully", Case.Sensitive);

	/// <summary>
	/// The new project is what the person never saw: an acceptance of the kind for the projects it covered
	/// does not cover a reason that also names one more.
	/// </summary>
	[Test]
	public void A_project_newly_failing_in_an_accepted_kind_is_still_said()
	{
		var notice = DegradedNotice(
			[Failures],
			[Heard(ReasonKinds.MsBuildLoadFailures, "Legacy.Tests", AgentsRead.Nothing)]);

		notice.ShouldNotBeNull();
		notice.ShouldContain("MSBuild reported 214 load failure(s)", Case.Sensitive);
	}

	[Test]
	public void A_new_kind_is_still_said_when_another_kind_is_accepted()
	{
		var restore = WorkspaceReason.Of(ReasonKinds.RestoreFailed, HealthEffect.BlocksAnswers, "Restore failed, so nothing resolved.");

		var notice = DegradedNotice([Legacy, restore], [Heard(ReasonKinds.ProjectNotLoaded, "Legacy.Tests", AgentsRead.Nothing)]);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Restore failed, so nothing resolved.", Case.Sensitive);
		notice.ShouldNotContain("besides", Case.Sensitive);
	}

	/// <summary>The reason a caller knows nothing about leads, so a known one accepted as Rose's line cannot push it into the count.</summary>
	[Test]
	public void An_unaccepted_reason_leads_an_accepted_one()
	{
		var restore = WorkspaceReason.Of(ReasonKinds.RestoreFailed, HealthEffect.BlocksAnswers, "Restore failed, so nothing resolved.");

		var notice = DegradedNotice([Legacy, restore], [Heard(ReasonKinds.ProjectNotLoaded, "Legacy.Tests", AgentsRead.RoseLine)])!;

		notice.ShouldContain("Restore failed, so nothing resolved. 1 more reason besides.", Case.Sensitive);
	}

	/// <summary>Where the projects of one reason are heard differently, the caller is owed the most any of them says.</summary>
	[Test]
	public void Projects_heard_differently_say_the_most_any_of_them_says()
	{
		var hearing = HearingOf(
			Failures,
			[
				Heard(ReasonKinds.MsBuildLoadFailures, "Legacy.Tests", AgentsRead.Nothing),
				Heard(ReasonKinds.MsBuildLoadFailures, "Ink.Tests", AgentsRead.Note, "Known."),
			]);

		hearing.ShouldNotBeNull();
		hearing.Read.ShouldBe(AgentsRead.Note);
	}

	/// <summary>A note never degrades a workspace, so it never reaches a read whatever is accepted.</summary>
	[Test]
	public void A_note_is_never_said_on_a_read() =>
		DegradedNotice([WorkspaceReason.Of(ReasonKinds.StaleBuildOutput, HealthEffect.Note, "Stale.")], []).ShouldBeNull();

	/// <summary>What the broker writes into a call's <c>_meta</c> is what the worker reads back, enums as names included.</summary>
	[Test]
	public void The_hearing_a_broker_sends_is_the_hearing_a_worker_reads()
	{
		ReasonHearing[] sent =
		[
			Heard(ReasonKinds.ProjectNotLoaded, "Legacy.Tests", AgentsRead.Note, "Known."),
			Heard(ReasonKinds.RestoreFailed, string.Empty, AgentsRead.Nothing),
		];

		var meta = new JsonObject { [ReasonHearing.MetaKey] = JsonSerializer.SerializeToNode(sent, ContractJson.Options) };

		HeardReasons.Read(meta).ShouldBe(sent);
		meta[ReasonHearing.MetaKey]!.ToJsonString().ShouldContain("\"read\":\"Note\"", Case.Sensitive);
	}

	/// <summary>
	/// Anything a call sends that does not parse is heard as nothing accepted: the failure that costs a line
	/// the caller did not need, never the one that hides a reason it did.
	/// </summary>
	[Test]
	[Arguments("""{"rosemcp/heardReasons":"nonsense"}""")]
	[Arguments("""{"rosemcp/heardReasons":[{"kind":"project-not-loaded","read":"Shout"}]}""")]
	[Arguments("""{"rosemcp/heardReasons":[{"read":"Nothing"}]}""")]
	[Arguments("""{}""")]
	public void Anything_unreadable_is_heard_as_nothing_accepted(string meta) =>
		HeardReasons.Read(JsonNode.Parse(meta)!.AsObject()).ShouldBeEmpty();

	[Test]
	public async Task A_call_hears_what_it_was_sent_and_nothing_after()
	{
		var meta = new JsonObject
		{
			[ReasonHearing.MetaKey] = JsonSerializer.SerializeToNode(
				new[] { Heard(ReasonKinds.RestoreFailed, string.Empty, AgentsRead.Nothing) },
				ContractJson.Options),
		};

		using (HeardReasons.Begin(meta))
		{
			await Task.Yield();
			HeardReasons.Current.ShouldHaveSingleItem().Kind.ShouldBe(ReasonKinds.RestoreFailed);
		}

		HeardReasons.Current.ShouldBeEmpty();
	}

	private static ReasonHearing Heard(string kind, string project, AgentsRead read, string? note = null) => new()
	{
		Kind = kind,
		Project = project,
		Read = read,
		Note = note,
	};
}
