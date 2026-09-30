using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Where a type could be cut. The answer that matters most is the empty one: a tool that finds
/// structure in everything is a horoscope, so a type holding together has to come back saying so.
/// </summary>
public sealed class IslandsTests
{
	/// <summary>
	/// Two jobs in one class, found from the state alone. Neither half calls the other, so nothing
	/// in a call graph would separate them; what separates them is that no field is read by both.
	/// </summary>
	[Test]
	public async Task Finds_the_two_jobs_a_type_is_doing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await IslandService.IslandsAsync(
			snapshot,
			"Library.TwoJobs",
			filePath: null,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Name.ShouldBe("TwoJobs");
		type.Namespace.ShouldBe("Library");
		type.Islands.Count.ShouldBe(2);
		foreach (var island in type.Islands)
		{
			island.Kind.ShouldBe("state");
		}

		var delivery = type.Islands.Single(island => island.Members.Contains("Accept"));
		delivery.Members.ShouldBe(["Accept", "Deliver", "Delivered", "Pending"]);

		// The state it would take with it, which is what makes this an extraction rather than a move.
		delivery.Fields.ShouldBe(["_delivered", "_inbox"]);

		var failures = type.Islands.Single(island => island.Members.Contains("Fail"));
		failures.Members.ShouldBe(["Fail", "Failures", "Retries", "Retry"]);
		failures.Fields.ShouldBe(["_errors", "_retries"]);

		// Nothing is read widely enough to be the type's spine, which is the other half of the finding:
		// there is no shared state holding the two halves together.
		type.Spine.ShouldBeEmpty();

		// A state island has no single door, so nothing claims to own it.
		foreach (var island in type.Islands)
		{
			island.Owner.ShouldBeNull();
		}
	}

	/// <summary>
	/// The case a partition cannot see. Every path to three helpers runs through one method, while a
	/// fourth helper is reached from outside it too -- so the island is what one member owns rather
	/// than everything it can get to.
	/// </summary>
	[Test]
	public async Task Leaves_behind_the_helper_something_else_also_reaches()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await IslandService.IslandsAsync(
			snapshot,
			"Library.OneDoor",
			filePath: null,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();
		var island = type.Islands.ShouldHaveSingleItem();

		island.Kind.ShouldBe("reach");
		island.Owner.ShouldBe("Render");
		island.Members.ShouldBe(["Pad", "Render", "Trim", "Wrap"]);

		// Render reaches Indent, and so does Plain. A helper two doors reach belongs to neither, and
		// taking it would break the one left behind.
		island.Members.ShouldNotContain("Indent");

		// Line ranges, because collecting members scattered through a file is the part that costs.
		foreach (var span in island.Spans)
		{
			span.ShouldMatch(@"^\d+-\d+$");
		}
	}

	/// <summary>
	/// The common answer, and the one worth having. One field every member reads is a spine, not a
	/// seam, and reporting it as a split would be a finding invented to have one.
	/// </summary>
	[Test]
	public async Task Says_a_type_holds_together_rather_than_inventing_a_split()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await IslandService.IslandsAsync(
			snapshot,
			"Library.Cohesive",
			filePath: null,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Islands.ShouldBeEmpty();
		type.Spine.ShouldBe(["_lines"]);
		type.Members.ShouldBe(4);

		// Said out loud, because an empty list reads the same as a question that failed to run.
		result.Notices.ShouldContain(notice => notice.Contains("holds together", StringComparison.Ordinal));
	}

	/// <summary>
	/// Islands nest, because what a member owns contains what the members under it own. Both are
	/// reported and the inner one says which world it is inside, so two overlapping lists read as a
	/// choice about how far to go rather than as a contradiction.
	/// </summary>
	[Test]
	public async Task Says_which_island_a_smaller_one_sits_inside()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await IslandService.IslandsAsync(
			snapshot,
			"Library.Layered",
			filePath: null,
			TestContext.Current!.Execution.CancellationToken);

		var type = result.Types.ShouldHaveSingleItem();

		type.Islands.Count.ShouldBe(2);

		var whole = type.Islands.Single(island => island.Owner == "Publish");
		var part = type.Islands.Single(island => island.Owner == "Envelope");

		whole.Members.ShouldBe(["Body", "Envelope", "Footer", "Header", "Publish", "Sign", "Stamp"]);
		part.Members.ShouldBe(["Body", "Envelope", "Footer", "Header", "Stamp"]);

		// The outer one is inside nothing, and the inner one names what it is inside rather than
		// leaving the reader to compare two lists.
		whole.Within.ShouldBeNull();
		part.Within.ShouldBe("Publish");
		foreach (var member in part.Members)
		{
			whole.Members.ShouldContain(member);
		}
	}

	/// <summary>A path answers for every type the file declares, the same way an outline does.</summary>
	[Test]
	public async Task Answers_for_every_type_a_file_declares()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var result = await IslandService.IslandsAsync(
			snapshot,
			type: null,
			fixture.Path("Members", "Library", "Islands.cs"),
			TestContext.Current!.Execution.CancellationToken);

		result.Types.Select(one => one.Name).ShouldBe(["TwoJobs", "OneDoor", "Cohesive", "Layered"]);
	}

	/// <summary>
	/// Two ways of choosing what to read, and naming both is a question rather than an instruction.
	/// </summary>
	[Test]
	public async Task Refuses_to_guess_between_a_type_and_a_file()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var refusal = await Should.ThrowAsync<ArgumentException>(() => IslandService.IslandsAsync(
			snapshot,
			type: null,
			filePath: null,
			TestContext.Current!.Execution.CancellationToken)).OfExactType();

		refusal.Message.ShouldContain("Namespace.Type", Case.Sensitive);
	}
}
