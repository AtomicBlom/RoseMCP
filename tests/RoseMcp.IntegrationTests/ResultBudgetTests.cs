using System.Text.Json;

using ModelContextProtocol;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What an answer costs the caller who has to read it, per item.
/// <para>
/// The unit suite's <c>ToolBudgetTests</c> measures what the model is shown before it calls
/// anything; this measures what comes back, which is where most of the cost is. An agent that pays
/// for a large answer once and learns nothing it could not have grepped does not pay it twice.
/// Work that shrinks a result is unverifiable without a number, and a result nobody measures
/// silently grows.
/// </para>
/// <para>
/// Per item rather than per result, because a result is as big as the answer needs to be and the
/// question is what each item of it costs. Each ceiling sits just above what its shape measures: it
/// is a ratchet, so a change that shrinks a result lowers it, and the diff is the record of what the
/// change bought.
/// </para>
/// <para>
/// Measured over the record as the tool returns it, serialised with the MCP layer's own options --
/// which leave out nulls, so a field a result does not fill costs nothing here, as it costs nothing
/// on the wire. Attribution is added by the broker, so a real result carries about 150 bytes this
/// does not -- once per result, not per item.
/// </para>
/// </summary>
public sealed class ResultBudgetTests
{
	/// <summary>
	/// One outlined member at the tool's defaults, which costs 70: its name, kind, line and
	/// accessibility, and nothing else. Measured over the members alone, the type they belong to taken
	/// out, because what the type says once -- the file, the project, its declarations -- is the cost
	/// the members no longer pay. A field that every member of a type shares, added back per member,
	/// is what this exists to catch: the absolute path alone is longer than the rest of an entry.
	/// </summary>
	private const int PerOutlinedMember = 75;

	/// <summary>
	/// One member of a referenced assembly's type in rose_symbol_info, which costs 172 -- StringBuilder's
	/// 101 come to about 17 KB. Dearer than an outlined member because its signature is always given: a
	/// metadata member has no line, so overloads would otherwise be one name repeated. The signature is
	/// most of it, the containing type's full name included, which is the one field this could still
	/// shed.
	/// </summary>
	private const int PerMetadataMember = 180;

	/// <summary>
	/// One reference with previews off, which costs 235. Every hit repeats the absolute path and
	/// carries four facets the tool will not filter on, which is what makes an overflow answerable
	/// only with a bigger artefact -- so this is the number that falls when either is taken out.
	/// </summary>
	private const int PerReference = 240;

	/// <summary>
	/// A whole write result for adding a doc comment, which costs 1,499 -- the floor for an edit that
	/// introduces no diagnostic at all; one that does costs several times more. Most of it is the doc
	/// comment the caller composed, read back in the diff. An edit loop pays this per edit, which
	/// makes it the result whose size matters most.
	/// </summary>
	private const int PerWriteResult = 1550;

	/// <summary>
	/// The read shapes, measured against one loaded fixture, because a load is the expensive part of
	/// this test and one is enough for both.
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
		members.ShouldBeGreaterThan(0, "the fixture type should have members to measure");

		AssertWithin(
			PerOutlinedMember,
			Size(outline) - Size(outline with { Types = [.. outline.Types.Select(type => type with { Members = [] })] }),
			members,
			"an outlined member with signatures and documentation off");

		var references = await NavigationService.FindReferencesAsync(
			snapshot,
			new SymbolTarget { Symbol = "Library.Greeter" },
			200,
			TestContext.Current!.Execution.CancellationToken,
			includePreviews: false);

		var hits = references.References.Count + references.Definitions.Count;
		hits.ShouldBeGreaterThan(0, "the fixture should have references to measure");

		AssertWithin(
			PerReference,
			Size(references) - Size(references with { References = [], Definitions = [] }),
			hits,
			"a reference with previews off");

		var library = await NavigationService.DescribeAsync(
			snapshot,
			new SymbolTarget { Symbol = "System.Text.StringBuilder" },
			TestContext.Current!.Execution.CancellationToken);

		var listed = library.Members.ShouldNotBeNull().Count;
		listed.ShouldBeGreaterThan(0, "StringBuilder should have members to measure");

		AssertWithin(
			PerMetadataMember,
			Size(library) - Size(library with { Members = [] }),
			listed,
			"a referenced assembly's member, which always carries its signature");
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

		// A doc comment and nothing else, the cheapest edit there is: the caller composed
		// every character of it and pays to read it back in the diff.
		var result = await MemberEdits.ReplaceAsync(
			session,
			"Library.Greeter.Count",
			"/// <summary>How many greetings have been asked for.</summary>\npublic int Count { get; set; }");

		result.Applied.ShouldBeTrue($"the edit should apply; notices were '{string.Join(" | ", result.Notices)}'");

		AssertWithin(PerWriteResult, Size(result), 1, "a write result for a one-line edit");
	}

	/// <summary>
	/// The measurement and the sentence that explains a failure. A budget that fails with a number
	/// and no shape sends the reader to the wrong file.
	/// <para>
	/// The size passed in is the marginal one -- the result with the items in it, less the same
	/// result with none -- so the answer's own scaffold is not divided across however many items a
	/// fixture happens to have. That makes the number a property of the item's shape rather than of
	/// the fixture, which is what lets a change lower it and mean something.
	/// </para>
	/// </summary>
	private static void AssertWithin(int budget, int size, int items, string shape)
	{
		var each = size / items;

		each.ShouldBeLessThanOrEqualTo(budget,
			$"{shape} costs {each} bytes against a budget of {budget} ({size} bytes over {items} items). "
				+ "Lower the budget if this is a result being shrunk; otherwise the shape has grown.");
	}

	private static int Size<T>(T result) =>
		JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions).Length;
}
