using System.Text.Json;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What an answer costs the caller who has to read it, per item.
/// <para>
/// The unit suite's <c>ToolBudgetTests</c> measures what the model is shown before it calls
/// anything; nothing measured what came back. The surface it did not cover is where the cost
/// actually is: a compact outline of a 24-member type came back at 10.1 KB with both of its size
/// controls off, and an agent that pays that once and learns nothing it could not have grepped does
/// not pay it twice. Four cards in tier 3 are about result size, and without a number they are
/// unverifiable and silently regress.
/// </para>
/// <para>
/// Per item rather than per result, because a result is as big as the answer needs to be and the
/// question is what each item of it costs. The ceilings below are what the shapes cost now: they are
/// a ratchet for work that has not happened, so the cards that shrink these results lower them, and
/// the diff is the record of what the work bought.
/// </para>
/// <para>
/// Measured over the record as the tool returns it, serialised the way a structured result is, which
/// is within a few percent of the wire and stable enough to compare. Attribution is added by the
/// broker, so a real result carries about 150 bytes this does not -- once per result, not per item.
/// </para>
/// </summary>
public sealed class ResultBudgetTests
{
	/// <summary>
	/// One outlined member with both of the tool's size controls off, which costs 512. The tool's own
	/// description says to use it "instead of reading the file to find out what is in it", and at
	/// this size a grep answers the same question for a twentieth of it, because the location record
	/// on each member carries the absolute path, the whole source line, the containing member, the
	/// project and its test-ness. Card 11's target is under 120.
	/// </summary>
	private const int PerOutlinedMember = 520;

	/// <summary>
	/// One reference with previews off, which costs 275. Every hit repeats the absolute path and
	/// carries four facets the tool will not filter on, which is what makes an overflow answerable
	/// only with a bigger artefact. Card 11e is where both halves of that go.
	/// </summary>
	private const int PerReference = 280;

	/// <summary>
	/// A whole write result for adding a doc comment, which costs 1,895 -- the floor for an edit that
	/// introduces no diagnostic at all, where card 11b measured about 4,000 for one that did. Most of
	/// it is the doc comment the caller composed, read back in the diff. An edit loop pays this per
	/// edit, so it is the number tier 3 moves furthest.
	/// </summary>
	private const int PerWriteResult = 1950;

	/// <summary>
	/// The read shapes, measured against one loaded fixture because a load is the expensive part and
	/// sharing one is card 18's work rather than this test's.
	/// </summary>
	[Test]
	public async Task A_read_costs_no_more_per_item_than_its_budget()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);
		var snapshot = await session.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		var outline = await OutlineService.OutlineAsync(
			snapshot,
			"Library.TwoJobs",
			filePath: null,
			includeInherited: false,
			includeDocumentation: false,
			includeSignatures: false,
			TestContext.Current!.Execution.CancellationToken);

		var members = outline.Types.Sum(type => type.Members.Count);
		Assert.True(members > 0, "the fixture type should have members to measure");

		AssertWithin(
			PerOutlinedMember,
			Size(outline) - Size(outline with { Types = [] }),
			members,
			"an outlined member with signatures and documentation off");

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter" },
			200,
			TestContext.Current!.Execution.CancellationToken,
			includePreviews: false);

		var hits = references.References.Count + references.Definitions.Count;
		Assert.True(hits > 0, "the fixture should have references to measure");

		AssertWithin(
			PerReference,
			Size(references) - Size(references with { References = [], Definitions = [] }),
			hits,
			"a reference with previews off");
	}

	/// <summary>
	/// And the write shape, which is the one an edit loop pays over and over. Its own fixture, since
	/// it changes the files it measures against.
	/// </summary>
	[Test]
	public async Task A_write_costs_no_more_than_its_budget()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// A doc comment and nothing else, which is the edit card 11b measured: the caller composed
		// every character of it and pays to read it back in the diff.
		var result = await MemberEdits.ReplaceAsync(
			session,
			"Library.Greeter.Count",
			"/// <summary>How many greetings have been asked for.</summary>\npublic int Count { get; set; }");

		Assert.True(result.Applied, $"the edit should apply; notices were '{string.Join(" | ", result.Notices)}'");

		AssertWithin(PerWriteResult, Size(result), 1, "a write result for a one-line edit");
	}

	/// <summary>
	/// The measurement and the sentence that explains a failure. A budget that fails with a number
	/// and no shape sends the reader to the wrong file.
	/// <para>
	/// The size passed in is the marginal one -- the result with the items in it, less the same
	/// result with none -- so the answer's own scaffold is not divided across however many items a
	/// fixture happens to have. That makes the number a property of the item's shape rather than of
	/// the fixture, which is what lets a card lower it and mean something.
	/// </para>
	/// </summary>
	private static void AssertWithin(int budget, int size, int items, string shape)
	{
		var each = size / items;

		Assert.True(
			each <= budget,
			$"{shape} costs {each} bytes against a budget of {budget} ({size} bytes over {items} items). "
				+ "Lower the budget if this is a result being shrunk; otherwise the shape has grown.");
	}

	private static int Size<T>(T result) =>
		JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
}
