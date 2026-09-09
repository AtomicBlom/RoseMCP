using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// One multi-line raw string literal, through every path that writes C# by name. Each test supplies
/// the same literal and reads the file back, because what a literal says and what its lines look
/// like are two different questions and only one of them has anything downstream watching it.
/// <para>
/// A raw literal's value is what is left once the closing delimiter's indentation comes off every
/// line, so shifting the content and the delimiter together leaves the value identical -- and
/// shifting one without the other rewrites the string with no parse error, no analyzer complaint
/// and nothing in the diff that reads as a change to a value. A blank line inside one is content of
/// the same kind: the compiler trims a whitespace-only line to nothing whatever it holds, so padding
/// one is invisible to the value and still not the text that was supplied.
/// </para>
/// <para>
/// Every literal here is written with CRLF, which is the fixture's own ending. That is deliberate:
/// code carrying one CRLF has every ending left exactly as it arrived, so these tests measure what
/// happens to a literal's indentation without the ending rewrite moving underneath them.
/// </para>
/// </summary>
public sealed class WrittenLiteralTests
{
	/// <summary>The literal as supplied, at a baseline of its own and with the fixture's endings.</summary>
	private const string Supplied = "\"\"\"\r\n\t\tfirst\r\n\r\n\t\tsecond\r\n\t\t\"\"\"";

	/// <summary>
	/// A literal in a member written whole. It lands a level in from where it was written, since the
	/// member does, and the value has to survive that.
	/// </summary>
	[Test]
	public async Task Replacing_a_member_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Literal.Report",
			Code = $"public static string Report()\r\n{{\r\n\treturn {Supplied};\r\n}}",
		});

		Assert.True(result.Applied);

		await AssertLiteralAsync(fixture, result, indent: "\t\t\t");
	}

	/// <summary>
	/// A member added rather than replaced. The same shift, on the path that also inserts blank lines
	/// and indentation of its own.
	/// </summary>
	[Test]
	public async Task Adding_a_member_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Add,

			// A type with no literal of its own, so the one this reads back is the one just written.
			Symbol = "Library.Prose",
			Code = $"public static string Second()\r\n{{\r\n\treturn {Supplied};\r\n}}",
		});

		Assert.True(result.Applied);

		await AssertLiteralAsync(fixture, result, indent: "\t\t\t", file: "Prose.cs");
	}

	/// <summary>
	/// A body supplied whole, where the signature stays as it is and only what follows it moves. The
	/// body's own baseline is read from the code, so the literal shifts by two levels rather than one.
	/// </summary>
	[Test]
	public async Task Replacing_a_block_body_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Literal.Report",
			Code = $"{{\r\n\treturn {Supplied};\r\n}}",
		});

		Assert.True(result.Applied);

		await AssertLiteralAsync(fixture, result, indent: "\t\t\t");
	}

	/// <summary>
	/// The same body written with an arrow. An expression body is a continuation Roslyn's formatter
	/// has no rule about, so whatever the shift does to it is what reaches disk.
	/// </summary>
	[Test]
	public async Task Replacing_an_expression_body_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Literal.Report",
			Code = $"=> {Supplied};",
		});

		Assert.True(result.Applied);

		await AssertLiteralAsync(fixture, result, indent: "\t\t");
	}

	/// <summary>
	/// A literal arriving through the anchored payload, which splices what it is given rather than
	/// re-indenting a whole member. Its lines are the caller's, so nothing here moves them.
	/// </summary>
	[Test]
	public async Task Replacing_part_of_a_body_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Prose.Label",
			Find = "\"total\"",
			Replace = Supplied,
		});

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Prose.cs");

		// Spliced exactly as written: the first line lands at the anchor and every line after it keeps
		// the indentation the caller gave it, because a literal's leading whitespace is its value.
		Assert.Equal(Supplied, Block(text));
		Assert.Equal("first\r\n\r\nsecond", Value(text));
	}

	/// <summary>
	/// A literal inside an attribute argument, which goes through the same splice as the anchored
	/// payload and for the same reason.
	/// </summary>
	[Test]
	public async Task Setting_an_attribute_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new DeclarationEditRequest
		{
			Symbol = "Library.Literal.Report",
			Attribute = $"Note({Supplied})",
			Action = AttributeAction.Set,

			// The fixture declares no attribute of that name, and an unresolved one reports an error
			// that says nothing about where the literal landed.
			Verify = false,
		};

		var result = await session.MutateAsync(
			(snapshot, token) => DeclarationEditService.SetAttributeAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Literal.cs");

		Assert.Equal(Supplied, Block(text));
		Assert.Equal("first\r\n\r\nsecond", Value(text));
	}

	/// <summary>
	/// A literal already on disk, moved into a type one level deeper. Nothing about it was supplied,
	/// so a value that changes here changes for a caller who never mentioned a string at all.
	/// </summary>
	[Test]
	public async Task Moving_a_member_keeps_what_its_literal_says()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new MoveMemberRequest
		{
			Symbol = "Library.Literal.Report",
			TargetType = "Library.Deep.Outer.Inner",
			CallSites = CallSiteStyle.Qualify,
		};

		var result = await session.MutateAsync(
			(snapshot, token) => MoveMemberService.MoveAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);

		Assert.True(result.Applied);

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", Path.Combine("Deep", "Outer.cs")),
			TestContext.Current!.Execution.CancellationToken);

		// One level deeper than it sat, which is how much deeper the member itself now sits. The
		// literal keeps its place inside the member rather than being flattened onto it.
		Assert.Equal("\"\"\"\r\n\t\t\t\t\t\t\tfirst\r\n\r\n\t\t\t\t\t\t\tsecond\r\n\t\t\t\t\t\t\t\"\"\"", Block(text));
		Assert.Equal("first\r\n\r\nsecond", Value(text));
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("re-indented", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// The literal as the file now holds it: at <paramref name="indent"/>, with its blank line still
	/// blank, saying exactly what it said when it was supplied, and either identical to what was
	/// supplied or reported as having moved.
	/// </summary>
	private static async Task AssertLiteralAsync(
		FixtureSolution fixture,
		MemberEditResult result,
		string indent,
		string file = "Literal.cs")
	{
		var text = await ReadAsync(fixture, file);
		var block = Block(text);

		Assert.Equal($"\"\"\"\r\n{indent}first\r\n\r\n{indent}second\r\n{indent}\"\"\"", block);
		Assert.Equal("first\r\n\r\nsecond", Value(text));

		// Either half of the promise is acceptable and neither may be skipped: a literal whose text
		// changed with nothing said about it is the failure this sweep is for. The value survives, no
		// analyzer reads a literal's interior, and the diff shows the member rewritten around it either
		// way -- so a caller comparing what they sent against what landed finds a difference and no
		// explanation.
		var said = result.Notices.Any(
			notice => notice.Contains("re-indented", StringComparison.OrdinalIgnoreCase));

		if (string.Equals(block, Supplied, StringComparison.Ordinal))
		{
			Assert.False(said, "The literal came out exactly as supplied, and the result claims it moved.");

			return;
		}

		Assert.True(said, $"The literal moved and nothing said so. Supplied [{Supplied}], landed [{block}].");
	}

	/// <summary>
	/// What the file's first multi-line raw literal says, worked out the way the compiler works it
	/// out: the closing delimiter's indentation off every line, and a whitespace-only line trimmed to
	/// nothing.
	/// </summary>
	private static string Value(string text)
	{
		var opened = text.IndexOf("\"\"\"\r\n", StringComparison.Ordinal);

		Assert.True(opened >= 0, "The file holds no multi-line raw literal.");

		var body = text[(opened + 5)..];
		var closed = body.IndexOf("\"\"\"", StringComparison.Ordinal);

		Assert.True(closed >= 0, "The literal is never closed.");

		var lines = body[..closed].Split("\r\n");
		var stripping = lines[^1];

		return string.Join(
			"\r\n",
			lines[..^1].Select(line => line.Trim().Length == 0
				? string.Empty
				: line.StartsWith(stripping, StringComparison.Ordinal) ? line[stripping.Length..] : line));
	}

	/// <summary>
	/// The file's first multi-line raw literal as text, opening delimiter through closing one, so a
	/// failure shows what landed rather than only that something did not match.
	/// </summary>
	private static string Block(string text)
	{
		var opened = text.IndexOf("\"\"\"\r\n", StringComparison.Ordinal);

		Assert.True(opened >= 0, $"The file holds no multi-line raw literal: {text}");

		var closed = text.IndexOf("\"\"\"", opened + 5, StringComparison.Ordinal);

		Assert.True(closed >= 0, $"The literal is never closed: {text}");

		return text[opened..(closed + 3)];
	}

	private static Task<MemberEditResult> EditAsync(WorkspaceSession session, MemberEditRequest request)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		return session.MutateAsync(
			(snapshot, token) => MemberEditService.EditAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, string file) =>
		File.ReadAllTextAsync(fixture.Path("Members", "Library", file), TestContext.Current!.Execution.CancellationToken);
}
