using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// What a write says about the lines it changed that nothing it was asked to do reaches.
/// <para>
/// The first five are damage the writing tools still do, each one of the shapes the open fidelity
/// issues recorded, and each asserts the damage as well as the sentence naming it. When a tool stops
/// doing it, both assertions fail together, and the test becomes that tool's own: an edit that
/// changes only what it was asked to. The last is the other half, and the one that keeps the sentence
/// worth reading: ordinary edits of every kind, none of which may be told it reached further than it
/// was asked.
/// </para>
/// </summary>
public sealed class OverreachReportTests
{
	/// <summary>
	/// An insertion at the end of a body rebuilds the whole body from its statements, so the statements
	/// already there come back laid out again: the blank lines between them go, and the arguments they
	/// wrapped move with them (#217).
	/// </summary>
	[Test]
	public async Task Names_the_statements_an_insertion_at_the_end_rewrote()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await WriteAsync(fixture, "Routes.cs", """
			namespace Library;

			public static class Routes
			{
				public static List<string> Map()
				{
					var routes = new List<string>();

					routes.Add(
						string.Concat("/sessions", "/breakpoints"));

					routes.Add(
						string.Concat("/sessions", "/threads"));

					return routes;
				}
			}

			""");

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Routes.Map",
			Position = BodyPosition.End,
			Code = "routes.Sort();",
		});

		var text = await ReadAsync(fixture, "Routes.cs");

		text.ShouldContain("new List<string>();\r\n\t\troutes.Add(", Case.Sensitive);
		text.ShouldNotContain("\r\n\t\t\tstring.Concat(\"/sessions\", \"/threads\"));", Case.Sensitive);
		Reached(result.Notices, "Routes.cs").ShouldStartWith(
			"Routes.cs: lines 8, 10-11 and 13 changed.", Case.Sensitive);
	}

	/// <summary>
	/// A whole initialiser written into a constant whose value sat on the line below it: the value comes
	/// up onto the declaration's line, which nothing asked for (#217).
	/// </summary>
	[Test]
	public async Task Names_the_declaration_a_value_was_pulled_up_onto()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await WriteAsync(fixture, "Phrases.cs", """
			namespace Library;

			public static class Phrases
			{
				public const string Greeting =
					"Hello, "
						+ "world.";
			}

			""");

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(
			session, Request(MemberEditKind.ReplaceBody, "Library.Phrases.Greeting", "\"Goodbye, \"\n\t+ \"world.\""));

		var text = await ReadAsync(fixture, "Phrases.cs");

		text.ShouldContain("public const string Greeting = \"Goodbye, \"", Case.Sensitive);
		Reached(result.Notices, "Phrases.cs").ShouldStartWith("Phrases.cs: line 5 changed.", Case.Sensitive);
	}

	/// <summary>
	/// An anchored change to one element of a collection expression: the bracket that opened it on a
	/// line of its own comes up onto the declaration's line (#217).
	/// </summary>
	[Test]
	public async Task Names_the_line_a_bracket_was_pulled_up_from()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await WriteAsync(fixture, "Lists.cs", """
			namespace Library;

			public static class Lists
			{
				private static readonly string[] Names =
				[
					"first",
					"second",
				];
			}

			""");

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Lists.Names",
			Find = "\"second\",",
			Replace = "\"second\",\n\"third\",",
		});

		var text = await ReadAsync(fixture, "Lists.cs");

		text.ShouldContain("Names = [", Case.Sensitive);
		Reached(result.Notices, "Lists.cs").ShouldStartWith("Lists.cs: lines 5-6 changed.", Case.Sensitive);
	}

	/// <summary>
	/// An anchor spanning two statements with a comment between them: the comment is trivia the matching
	/// cannot see and find cannot carry, so nothing can have asked for it to go -- and it goes (#195).
	/// </summary>
	[Test]
	public async Task Names_the_comment_a_match_took_with_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await WriteAsync(fixture, "Steps.cs", """
			namespace Library;

			public static class Steps
			{
				public static int Run(int value)
				{
					value += 1;
					// The second step depends on the first.
					value *= 2;

					return value;
				}
			}

			""");

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Steps.Run",
			Find = "value += 1;\nvalue *= 2;",
			Replace = "value = (value + 1) * 2;",
		});

		var text = await ReadAsync(fixture, "Steps.cs");

		text.ShouldNotContain("depends on the first", Case.Sensitive);
		Reached(result.Notices, "Steps.cs").ShouldStartWith("Steps.cs: line 8 changed.", Case.Sensitive);
	}

	/// <summary>
	/// A body edit in a solution with no .editorconfig anywhere above it. The formatter falls back to its
	/// own four spaces in a file indented with tabs, and reaches the indentation in front of the next
	/// member as well as the one it wrote -- so a member the edit never named is re-indented (#218).
	/// </summary>
	[Test]
	public async Task Names_the_neighbour_a_body_edit_reindented_where_nothing_says_how_to_indent()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, Request(MemberEditKind.ReplaceBody, "Core.Calculator.Multiply", "=> right * left;"));

		var text = await File.ReadAllTextAsync(
			fixture.Path("Simple", "Core", "Calculator.cs"), TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("\r\n    private static int Twice", Case.Sensitive);
		Reached(result.Notices, "Calculator.cs").ShouldStartWith("Calculator.cs: line 9 changed.", Case.Sensitive);
	}

	/// <summary>
	/// Every kind of write, each in a shape where a diff could pair a line with the wrong neighbour -- a
	/// member between two others, values in an enum separated by blank lines, a new group of imports, a
	/// body that changes shape -- and none of them may be told it changed a line it was not asked to.
	/// A sentence that fires on correct edits is one nobody reads, which would leave the damage it
	/// exists to name unreported again.
	/// </summary>
	[Test]
	public async Task Says_nothing_about_edits_that_changed_only_what_they_were_asked_to()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);
		var token = TestContext.Current!.Execution.CancellationToken;

		var results = new List<(string Edit, IReadOnlyList<string> Notices)>
		{
			("add a member between two others", (await EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.Add,
				Symbol = "Library.Greeter",
				After = "PrefixLength",
				Code = "public string Wave() => \"Hi\";",
			})).Notices),
			("replace a member", (await ReplaceAsync(
				session, "Library.Greeter.Shout", "private static string Shout(string text)\n{\n\treturn text.ToUpperInvariant() + \"!\";\n}")).Notices),
			("give a block body an arrow", (await EditAsync(
				session, Request(MemberEditKind.ReplaceBody, "Library.Greeter.Greet(string)", "=> $\"{_prefix}, {name}.\";"))).Notices),
			("insert in front of a closing return", (await EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.ReplaceBody,
				Symbol = "Library.Wrapped.Join",
				Position = BodyPosition.End,
				Code = "first = first.Trim();",
			})).Notices),
			("delete a member between blank lines", (await EditAsync(
				session, Request(MemberEditKind.Delete, "Library.Regioned.Thrice", string.Empty))).Notices),
			("add a value to an enum laid out with blank lines", (await EditAsync(session, new MemberEditRequest
			{
				Kind = MemberEditKind.Add,
				Symbol = "Library.Colour",
				After = "Red",
				Code = "Orange = 3,",
			})).Notices),
		};

		results.Add(("replace a documentation comment", (await session.MutateAsync(
			(snapshot, cancel) => DeclarationEditService.ReplaceDocCommentAsync(
				snapshot,
				diagnostics,
				new DeclarationEditRequest { Symbol = "Library.Greeter.Count", Comment = "How many there were." },
				session.NoteSelfWrite,
				cancel),
			token)).Notices));

		results.Add(("add an attribute", (await session.MutateAsync(
			(snapshot, cancel) => DeclarationEditService.SetAttributeAsync(
				snapshot,
				diagnostics,
				new DeclarationEditRequest { Symbol = "Library.Greeter.Count", Attribute = "Obsolete", Action = AttributeAction.Add },
				session.NoteSelfWrite,
				cancel),
			token)).Notices));

		results.Add(("start a new group of imports", (await session.MutateAsync(
			(snapshot, cancel) => AddUsingService.AddAsync(
				snapshot,
				diagnostics,
				new AddUsingRequest { FilePath = fixture.Path("Members", "Library", "Imports.cs"), Namespaces = ["static System.Math"] },
				session.NoteSelfWrite,
				cancel),
			token)).Notices));

		results.Add(("add a parameter", (await session.MutateAsync(
			(snapshot, cancel) => ChangeSignatureService.ChangeAsync(
				snapshot,
				diagnostics,
				new ChangeSignatureRequest { Symbol = "Library.Arrowed.Describe", Parameters = "string first, string second, bool loud = false" },
				session.NoteSelfWrite,
				cancel),
			token)).Notices));

		foreach (var (edit, notices) in results)
		{
			var reached = notices.FirstOrDefault(IsOverreach);

			(reached is null).ShouldBeTrue($"{edit}: {reached}");
		}
	}

	private static Task WriteAsync(FixtureSolution fixture, string file, string text) =>
		File.WriteAllTextAsync(fixture.Path("Members", "Library", file), text, TestContext.Current!.Execution.CancellationToken);

	/// <summary>The sentence about <paramref name="file"/> saying it changed lines nothing asked for, if there is one.</summary>
	private static string? Reached(IReadOnlyList<string> notices, string file) =>
		notices.FirstOrDefault(notice => IsOverreach(notice) && notice.StartsWith($"{file}: ", StringComparison.Ordinal));

	private static bool IsOverreach(string notice) =>
		notice.Contains("Nothing this was asked to do reaches them", StringComparison.Ordinal);
}
