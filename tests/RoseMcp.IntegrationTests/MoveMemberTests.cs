using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Moving a member between types. One tool rather than an add and a delete, because those are two
/// writes and a failure between them leaves the member declared twice -- and because the call sites
/// are the part a person doing it by hand forgets, choosing once per file and differently each time.
/// </summary>
public sealed class MoveMemberTests
{
	[Fact]
	public async Task Moves_a_member_and_qualifies_its_call_sites()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Regioned.Twice", "Library.Greeter");

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var source = await ReadAsync(fixture, "Regioned.cs");
		var target = await ReadAsync(fixture, "Greeter.cs");
		var caller = await ReadAsync(fixture, "Builds.cs");

		Assert.DoesNotContain("Twice", source, StringComparison.Ordinal);
		Assert.Contains("public static int Twice(int value)", target, StringComparison.Ordinal);

		// The call site in another file now names the new home, which is the half a person forgets.
		Assert.Contains("Greeter.Twice(21)", caller, StringComparison.Ordinal);
		Assert.Contains("call site(s) now name Greeter", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}

	/// <summary>
	/// The documentation comment goes with the declaration. Leaving it behind would leave a summary
	/// describing something that is not there, above whatever the next member turns out to be.
	/// </summary>
	[Fact]
	public async Task Takes_the_documentation_comment_with_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, "Library.Regioned.Thrice", "Library.Greeter");

		var source = await ReadAsync(fixture, "Regioned.cs");
		var target = await ReadAsync(fixture, "Greeter.cs");

		Assert.DoesNotContain("Trebles it", source, StringComparison.Ordinal);
		Assert.Contains("Trebles it", target, StringComparison.Ordinal);

		// The region it left is still balanced.
		Assert.Contains("#region Helpers", source, StringComparison.Ordinal);
		Assert.Contains("#endregion", source, StringComparison.Ordinal);
	}

	/// <summary>
	/// A hand-wrapped signature keeps its shape through a move, re-indented for the type it lands in
	/// rather than deepened by it.
	/// <para>
	/// The member arrives as its own source text with the first line's indentation trimmed off, which
	/// reads as a baseline of nothing while every continuation still carries the old type's. The
	/// destination's indentation then goes on top of indentation that is already there, and the list
	/// lands a level deeper than the member it belongs to. Nothing downstream reports it: a
	/// continuation line is not a statement, so Roslyn's formatter has no rule that moves one, and
	/// neither IDE0055 nor <c>dotnet format</c> has an opinion about where a wrapped list sits.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Keeps_the_shape_of_a_wrapped_signature_it_moves()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Wrapped.Join", "Library.Greeter");

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var target = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the parameters it wrapped onto their own lines.
		Assert.Contains(
			"\tpublic static string Join(\r\n\t\tstring first,\r\n\t\tstring second,\r\n\t\tstring third)\r\n\t{\r\n",
			target,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// An expression body wrapped across lines keeps its shape through a move, re-indented for the
	/// type it lands in rather than deepened or flattened by it. The <c>=&gt;</c> and the lines under
	/// it are continuations, which Roslyn's formatter has no rule about.
	/// </summary>
	[Fact]
	public async Task Keeps_the_shape_of_a_wrapped_expression_body_it_moves()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Arrowed.Spread", "Library.Greeter");

		Assert.True(result.Applied, "the move is written; only its layout is under test");
		Assert.Empty(result.IntroducedDiagnostics);

		var target = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the body, three for the lines it wraps onto.
		Assert.Contains(
			"\tpublic static string Spread(string first, string second, string third) =>\r\n\t\tfirst"
				+ "\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \", \" + third;",
			target,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// A moved member arrives separated from the one above it, the same as one that is added.
	/// Roslyn's formatter reindents and moves braces but never inserts a blank line between members,
	/// so a member appended without one lands flush against the closing brace above it and no rule
	/// anywhere puts it back.
	/// </summary>
	[Fact]
	public async Task Separates_the_member_it_moves_from_the_one_above_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Wrapped.Join", "Library.Greeter");

		Assert.True(result.Applied);

		var target = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("\t}\r\n\r\n\tpublic static string Join(", target, StringComparison.Ordinal);
	}

	/// <summary>
	/// The other call-site style: the calls stay as written and each calling file imports the new
	/// home statically. Smaller diff, at the cost of a file whose calls no longer say where they go.
	/// </summary>
	[Fact]
	public async Task Imports_the_new_home_instead_of_qualifying()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(
			session, "Library.Regioned.Twice", "Library.Greeter", CallSiteStyle.UsingStatic);

		Assert.True(result.Applied);
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("left as written", StringComparison.Ordinal));
	}

	/// <summary>
	/// An instance member's move changes what 'this' means inside it, and every call site would need
	/// a receiver it has no reason to have to hand. Refused rather than half done.
	/// </summary>
	[Fact]
	public async Task Refuses_an_instance_member()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => MoveAsync(session, "Library.Greeter.Greet(string)", "Library.Regioned"));

		Assert.Contains("is an instance member", thrown.Message, StringComparison.Ordinal);
		Assert.Contains("Make it static first", thrown.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	/// <summary>Moving a member to where it already is is a mistake worth naming rather than a no-op.</summary>
	[Fact]
	public async Task Refuses_a_move_to_the_type_it_is_already_in()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Assert.ThrowsAsync<ArgumentException>(
			() => MoveAsync(session, "Library.Regioned.Twice", "Library.Regioned"));

		Assert.Contains("is already in", thrown.Message, StringComparison.Ordinal);
	}

	private static Task<MemberEditResult> MoveAsync(
		WorkspaceSession session,
		string symbol,
		string targetType,
		CallSiteStyle callSites = CallSiteStyle.Qualify)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new MoveMemberRequest
		{
			Symbol = symbol,
			TargetType = targetType,
			CallSites = callSites,
		};

		return session.MutateAsync(
			(snapshot, token) => MoveMemberService.MoveAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, params string[] parts) =>
		File.ReadAllTextAsync(
			fixture.Path("Members", "Library", string.Join(Path.DirectorySeparatorChar, parts)),
			TestContext.Current.CancellationToken);
}
