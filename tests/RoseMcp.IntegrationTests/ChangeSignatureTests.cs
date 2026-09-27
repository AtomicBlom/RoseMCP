using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Changing a signature, which is the same capability as renaming and the same argument for it:
/// find-and-replace gets a rename wrong, and grep-and-edit-per-layer gets this wrong.
/// <para>
/// The fixture has the shape that made it necessary -- an interface, a base, an override that calls
/// its parameter something else, and a forwarder -- because the failures are all about the
/// declarations and call sites somebody doing it by hand does not think of.
/// </para>
/// </summary>
public sealed class ChangeSignatureTests
{
	/// <summary>
	/// The common case by a wide margin: an optional flag added to an existing method. No call site
	/// has to change, which is exactly why every one of them is reported -- a caller that goes on
	/// taking the default may be one that should not, and nothing about the build would say so.
	/// </summary>
	[Test]
	public async Task Adds_an_optional_parameter_and_reports_the_call_sites_it_left()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Greeter.Greet(string)", "string name, bool loud = false");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Greeter.cs");

		text.ShouldContain("public string Greet(string name, bool loud = false)", Case.Sensitive);

		// Caller.cs calls it and needed nothing, and is named anyway.
		result.UnchangedCallSites.ShouldContain(
			site => site.Location.FilePath.EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase)
				&& site.Reason.Contains("every new parameter has a default", StringComparison.Ordinal));
	}

	/// <summary>
	/// A parameter list the caller wrapped lands one level in from the declaration it belongs to, one
	/// parameter to a line, and gets there whichever of the five ways they wrote it: flat under a
	/// leading line break, indented relative to itself, already indented for where it goes, with the
	/// first parameter indented alongside the rest, or with the first flush and the rest under it.
	/// Those are one request.
	/// <para>
	/// The declaration's own indentation is a level short, because a continuation is not a sibling of
	/// the signature, and the first line is the half that had no rule at all: it landed inline after
	/// the parenthesis carrying its own indentation, or -- where the declaration was already wrapped
	/// and the parenthesis held the break -- at column zero. Nothing downstream corrects any of it.
	/// A continuation line is not a statement, so Roslyn's formatter has no rule that moves one, and
	/// neither IDE0055 nor <c>dotnet format</c> has an opinion about where a wrapped list sits: the
	/// list comes out however it landed and every build passes.
	/// </para>
	/// </summary>
	[Test]
	[Arguments("\nstring first,\nstring second,\nstring third,\nstring fourth = \"\"")]
	[Arguments("\n\tstring first,\n\tstring second,\n\tstring third,\n\tstring fourth = \"\"")]
	[Arguments("\n\t\tstring first,\n\t\tstring second,\n\t\tstring third,\n\t\tstring fourth = \"\"")]
	[Arguments("\tstring first,\n\tstring second,\n\tstring third,\n\tstring fourth = \"\"")]
	[Arguments("string first,\n\tstring second,\n\tstring third,\n\tstring fourth = \"\"")]
	public async Task Wraps_a_parameter_list_a_level_in_from_the_declaration(string written)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Wrapped.Join(string, string, string)", written);

		result.Applied.ShouldBeTrue("the change is written; only its layout is under test");
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Wrapped.cs");

		// One tab for the member, two for the parameters it wrapped onto their own lines.
		text.ShouldContain(
			"\tpublic static string Join(\r\n\t\tstring first,\r\n\t\tstring second,\r\n\t\tstring third,"
				+ "\r\n\t\tstring fourth = \"\")\r\n", Case.Sensitive);
	}

	/// <summary>
	/// The other half of the same rule: a list the caller wrote on one line goes on the signature
	/// line, even where the declaration it replaces was wrapped and its parenthesis still carries the
	/// break. Left there, that break puts the first parameter alone on a line of its own at whatever
	/// column the caller's text happened to begin at.
	/// </summary>
	[Test]
	public async Task Unwraps_a_parameter_list_the_caller_wrote_on_one_line()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Wrapped.Join(string, string, string)", "string first, string second");

		result.Applied.ShouldBeTrue("the change is written; only its layout is under test");

		var text = await ReadAsync(fixture, "Wrapped.cs");

		text.ShouldContain("\tpublic static string Join(string first, string second)\r\n", Case.Sensitive);
	}

	/// <summary>
	/// A signature change on a member whose expression body is wrapped. The parameters are this
	/// tool's business and the body is not, but the whitespace pass runs over the lines the change
	/// wrote -- so the body is what says whether it reached past them.
	/// </summary>
	[Test]
	public async Task Leaves_a_wrapped_expression_body_alone_when_it_changes_the_parameters()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Arrowed.Spread", "string first, string second, string third, string fourth = \"\"");

		result.Applied.ShouldBeTrue("the change is written; the body is what is under test");
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Arrowed.cs");

		// Two tabs for the body, three for the lines it wraps onto, exactly as before.
		text.ShouldContain(
			"\t\tfirst\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \", \" + third;", Case.Sensitive);
	}

	/// <summary>
	/// Issue #59, end to end. A call site that already writes an argument for the parameter being
	/// added does not compile as it stands -- which is the ordinary reason to reach for this tool -- and
	/// its surplus argument was silently deleted, leaving a call that compiles and means something
	/// else. Left alone, the very change being made is the one that makes it correct, which is what the
	/// last two assertions check: nothing was introduced, and something was resolved.
	/// <para>
	/// The fixture is written here rather than checked in, because a call site that does not compile
	/// would fail every other test that loads this solution.
	/// </para>
	/// </summary>
	[Test]
	public async Task Leaves_the_argument_a_call_site_already_wrote_for_the_new_parameter()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		// One overload, so the call site is unambiguously this method's even though it does not bind.
		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "Anticipating.cs"),
			"""
		namespace Library;

		public static class Anticipating
		{
			public static string Describe(string text) => text;

			public static string Call() => Describe("x", true);
		}

		""",
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Anticipating.Describe(string)", "string text, bool loud = false");

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Anticipating.cs");

		// The argument is still there. This is the whole card: it used to come back as Describe("x").
		text.ShouldContain("Describe(\"x\", true)", Case.Sensitive);

		// Reported as one it did not touch, and reported once. It used to appear in both lists at
		// once, the second time with a reason that was not what had happened to it.
		result.UnchangedCallSites.ShouldContain(
			site => site.Location.FilePath.EndsWith("Anticipating.cs", StringComparison.OrdinalIgnoreCase)
				&& site.Reason.Contains("does not compile as it stands", StringComparison.Ordinal)
				&& site.Reason.Contains("may already be right", StringComparison.Ordinal));

		result.UpdatedCallSites.ShouldNotContain(
			site => site.FilePath.EndsWith("Anticipating.cs", StringComparison.OrdinalIgnoreCase));

		var updated = result.UpdatedCallSites.Select(site => (site.FilePath, site.Line));
		var unchanged = result.UnchangedCallSites.Select(site => (site.Location.FilePath, site.Location.Line));
		updated.Intersect(unchanged).ShouldBeEmpty();

		// Leaving it alone is what makes it compile: the parameter it was written for now exists.
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.ResolvedDiagnosticCount.ShouldBeGreaterThan(0, "expected the call site's error to go away");
	}

	/// <summary>
	/// A required parameter, which every call site does have to change. The argument is written as
	/// a named one, because that is valid wherever it lands and needs no reasoning about position.
	/// </summary>
	[Test]
	public async Task Adds_a_required_parameter_and_passes_it_at_every_call_site()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Greeter.Greet(string)", "string name, bool loud", ["loud=false"]);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var caller = await ReadAsync(fixture, "Caller.cs");

		// Positionally, because it lands in its own slot: a named argument is only needed once
		// something before it has been omitted or moved.
		caller.ShouldContain("Greet(\"world\", false)", Case.Sensitive);
		result.UpdatedCallSites.ShouldHaveSingleItem();
	}

	/// <summary>
	/// Refused before anything is written. A required parameter with nothing to pass would break
	/// every call site, and which of the two the caller meant is not something to guess at.
	/// </summary>
	[Test]
	public async Task Refuses_a_required_parameter_with_nothing_to_pass()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var error = await Should.ThrowAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Greet(string)", "string name, bool loud")).OfExactType();

		error.Message.ShouldContain("loud would be required", Case.Sensitive);
		error.Message.ShouldContain("name=expression", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
	}

	/// <summary>
	/// The same guard, on a solution where the call sites already pass the new argument. That is what
	/// an author writing the call before the parameter has, and the refusal was false of exactly those
	/// sites: they have something to pass, and the change is what they are waiting for. It fired
	/// before any call site had been read, so it could not know.
	/// <para>
	/// The sites are left exactly as written, because a call site that does not bind is one where
	/// nothing is known about which argument means what -- and each is reported, so a site that was
	/// broken for some other reason is not quietly counted as fixed.
	/// </para>
	/// </summary>
	[Test]
	public async Task Adds_a_required_parameter_when_the_call_sites_already_pass_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// The call site written ahead of the parameter, which is the shape this is about.
		await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Library.Arrowed.Call",
			Code = "=> Describe(\"one\", \"two\", \"three\");",
			Verify = false,
		});

		var result = await ChangeAsync(
			session,
			"Library.Arrowed.Describe(string, string)",
			"string first, string second, string third");

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Arrowed.cs");

		text.ShouldContain("string third", Case.Sensitive);
		text.ShouldContain("Describe(\"one\", \"two\", \"three\")", Case.Sensitive);

		// Reported rather than silently skipped, and it compiles now that the parameter is there.
		result.UnchangedCallSites.ShouldNotBeEmpty();
		result.IntroducedDiagnostics.ShouldBeEmpty();
	}

	/// <summary>
	/// Nothing to break is not the same as something to break. A member no call site binds to takes a
	/// required parameter without argument, since the refusal exists to protect call sites and there
	/// are none.
	/// </summary>
	[Test]
	public async Task Adds_a_required_parameter_to_a_member_nothing_calls()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Arrowed.Spread", "string first, string second, string third, bool loud");

		result.Applied.ShouldBeTrue();
		(await ReadAsync(fixture, "Arrowed.cs")).ShouldContain("bool loud", Case.Sensitive);
		result.IntroducedDiagnostics.ShouldBeEmpty();
	}

	/// <summary>
	/// The declarations that have to move together. Changing only the one named does not compile,
	/// and the override calls its parameter something else -- so its own name has to survive, or
	/// the change would rename it without saying so.
	/// </summary>
	[Test]
	public async Task Moves_the_interface_the_base_and_the_override_together()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Notifier.Notify(string)", "string message, bool urgent = false");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Layers.cs");

		// The interface, the base, and the override -- which keeps calling its parameter text.
		text.ShouldContain("string Notify(string message, bool urgent = false);", Case.Sensitive);
		text.ShouldContain("public virtual string Notify(string message, bool urgent = false)", Case.Sensitive);
		text.ShouldContain("public override string Notify(string text, bool urgent = false)", Case.Sensitive);

		result.UpdatedDeclarations.Count.ShouldBe(3);
	}

	/// <summary>
	/// Where documentation is generated, a tag for a parameter that no longer exists is CS1572 and
	/// a parameter with no tag is CS1573 -- both errors in a repository that treats warnings as
	/// errors. So a change that left the tags alone would compile the code and break the build.
	/// </summary>
	[Test]
	public async Task Keeps_the_param_tags_in_step()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Notifier.Notify(string)", "string message, bool urgent = false");

		var text = await ReadAsync(fixture, "Layers.cs");

		text.ShouldContain("<param name=\"urgent\"></param>", Case.Sensitive);
		text.ShouldContain("<param name=\"message\">What to say.</param>", Case.Sensitive);
		result.DocumentationUpdated.ShouldNotBeEmpty();

		// And it says the tag it added has no description, which is not something to invent.
		result.Notices.ShouldContain(
			notice => notice.Contains("needs a description", StringComparison.Ordinal));
	}

	/// <summary>
	/// Removing a parameter takes its argument and its tag with it -- and reports the bodies that
	/// were using it, which is a thing no tool can fix and the caller has to decide about. The
	/// value is that it is one answer rather than a build, and it names both places.
	/// </summary>
	[Test]
	public async Task Removes_a_parameter_and_reports_the_bodies_that_used_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Notifier.Notify(string)", string.Empty);

		result.Applied.ShouldBeTrue();

		var text = await ReadAsync(fixture, "Layers.cs");

		// The declarations, the tag and the arguments all went.
		text.ShouldContain("public virtual string Notify()", Case.Sensitive);
		text.ShouldContain("public override string Notify()", Case.Sensitive);
		text.ShouldContain("string Notify();", Case.Sensitive);
		text.ShouldNotContain("<param name=\"message\">", Case.Sensitive);
		text.ShouldContain("notifier.Notify()", Case.Sensitive);

		// And the two bodies that still refer to the parameter are named, in one answer.
		result.IntroducedDiagnostics.Count.ShouldBe(2);
		foreach (var entry in result.IntroducedDiagnostics)
		{
			entry.Id.ShouldBe("CS0103");
		}
	}

	/// <summary>
	/// Reordering what is already there is refused, because an argument's meaning at a call site is
	/// not always recoverable from its position. Inserting a new parameter in the middle is not the
	/// same thing and is allowed.
	/// </summary>
	[Test]
	public async Task Refuses_to_reorder_parameters_that_already_exist()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Greet(string, string)", "string name, string title")).OfExactType();

		error.Message.ShouldContain("would move in front of", Case.Sensitive);
		error.Message.ShouldContain("New parameters can go anywhere", Case.Sensitive);
	}

	/// <summary>
	/// A new parameter in the middle: the arguments after it can no longer be positional, so they
	/// are written as named ones rather than left to bind to the wrong parameter.
	/// </summary>
	[Test]
	public async Task Names_the_arguments_a_new_parameter_displaces()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session,
			"Library.Greeter.Greet(string, string)",
			"string title, bool loud, string name",
			["loud=true"]);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);
	}

	/// <summary>
	/// Retyping changes nothing at the call sites, which is exactly why it is worth a warning: an
	/// argument that still converts will compile and mean something else.
	/// </summary>
	[Test]
	public async Task Says_when_a_parameter_changed_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Greeter.Greet(string)", "object name");

		result.Notices.ShouldContain(
			notice => notice.Contains("Retyped name", StringComparison.Ordinal));
	}

	[Test]
	public async Task Writes_nothing_when_previewing()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Layers.cs");

		var result = await session.MutateAsync(
			(snapshot, token) => ChangeSignatureService.ChangeAsync(
				snapshot,
				new DiagnosticsService(NullLogger<DiagnosticsService>.Instance),
				new ChangeSignatureRequest
				{
					Symbol = "Library.Notifier.Notify(string)",
					Parameters = "string message, bool urgent = false",
					Apply = false,
				},
				session.NoteSelfWrite,
				token),
			TestContext.Current!.Execution.CancellationToken);

		result.Applied.ShouldBeFalse("a preview writes nothing");
		(await ReadAsync(fixture, "Layers.cs")).ShouldBe(before);
		string.Join(" ", result.Notices).ShouldContain("Preview only", Case.Sensitive);
		result.Diff.ShouldContain("urgent", Case.Sensitive);
	}

	/// <summary>A member with no parameter list to change is told so rather than mangled.</summary>
	[Test]
	public async Task Declines_what_has_no_parameters()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Should.ThrowAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Count", "int value")).OfExactType();

		error.Message.ShouldContain("no parameter list", Case.Sensitive);
		error.Message.ShouldContain("rose_replace_member", Case.Sensitive);
	}

	private static Task<SignatureChangeResult> ChangeAsync(
		WorkspaceSession session,
		string symbol,
		string parameters,
		string[]? arguments = null)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new ChangeSignatureRequest
		{
			Symbol = symbol,
			Parameters = parameters,
			Arguments = arguments ?? [],
		};

		return session.MutateAsync(
			(snapshot, token) => ChangeSignatureService.ChangeAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	/// <summary>
	/// A member edit, for setting up a call site that is written before the parameter it passes.
	/// </summary>
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

	/// <summary>
	/// A constructor is where a parameter is added most often, and its declaration carries a name the
	/// language and the runtime spell differently. Both spellings reach it.
	/// </summary>
	[Test]
	[Arguments("Library.Assembled.Assembled(string)")]
	[Arguments("Library.Assembled..ctor(string)")]
	public async Task Changes_a_constructor_addressed_either_way(string symbol)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, symbol, "string name, int count = 1");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Constructed.cs");

		text.ShouldContain("public Assembled(string name, int count = 1)", Case.Sensitive);
	}

	/// <summary>
	/// A primary constructor's parameters are written on the type, so its only declaration is the type
	/// declaration. Nothing about that is visible in the symbol, which is a method like any other, and
	/// treating "not a method declaration" as "no parameter list" refuses the ordinary modern shape.
	/// </summary>
	[Test]
	public async Task Changes_a_primary_constructor_declared_on_the_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Composed.Composed(string)", "string name, int count = 1");

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.TotalErrorCount.ShouldBe(0);

		var text = await ReadAsync(fixture, "Constructed.cs");

		text.ShouldContain("public sealed class Composed(string name, int count = 1)", Case.Sensitive);
	}

	/// <summary>
	/// A required parameter breaks every call site that does not pass it, and the call site is in
	/// another file. What comes back names it rather than leaving it to a build.
	/// </summary>
	[Test]
	public async Task Rewrites_a_construction_in_another_file()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Assembled.Assembled(string)", "string name, int count", ["count=1"]);

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		result.UpdatedCallSites.ShouldContain(
			site => site.FilePath.EndsWith("Builds.cs", StringComparison.OrdinalIgnoreCase));

		var text = await File.ReadAllTextAsync(
			fixture.Path("Members", "Library", "Builds.cs"), TestContext.Current!.Execution.CancellationToken);

		text.ShouldContain("new Assembled(\"one\", 1)", Case.Sensitive);
	}

	/// <summary>
	/// A type with no constructor of its own has one the compiler writes, which is not in the file. The
	/// refusal says that rather than reporting the name as unknown.
	/// </summary>
	[Test]
	public async Task Refuses_a_constructor_the_compiler_wrote()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Greeter()", "int count")).OfExactType();

		thrown.Message.ShouldContain("written by the compiler", Case.Sensitive);
	}

	/// <summary>
	/// A forwarder is the shape this tool exists for and the one it cannot finish. A new parameter with
	/// a default breaks nothing, so the forwarder compiles while still passing the old default, and
	/// every caller of it silently gets the behaviour the change was meant to alter. Listed beside
	/// forty ordinary call sites, that is what lets a five-deep chain go half-changed.
	/// </summary>
	[Test]
	public async Task Says_which_unchanged_call_sites_are_forwarders()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.INotifier.Notify(string)", "string message, bool loud = false");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var forwarder = result.UnchangedCallSites.Where(
			site => site.Reason.Contains("whole body of Send", StringComparison.Ordinal)).ShouldHaveSingleItem();

		forwarder.Reason.ShouldContain("forwards its own parameters through", Case.Sensitive);
		forwarder.Reason.ShouldContain("Change", Case.Sensitive);

		// A method that happens to contain a call is not a forwarder: SendTwice calls it twice and
		// concatenates, so calling that mechanical would be telling the caller something untrue.
		result.UnchangedCallSites.ShouldNotContain(
			site => site.Reason.Contains("whole body of SendTwice", StringComparison.Ordinal));
	}

	/// <summary>
	/// The two shapes that name the member without calling it. Neither can be rewritten -- a
	/// <c>nameof</c> carries no arguments to put back, and a method group's shape belongs to the
	/// delegate type it converts to rather than to the call -- and the conversion stops compiling
	/// the moment the signature moves, so passing over them silently leaves the caller to find it
	/// from a build.
	/// </summary>
	[Test]
	public async Task Reports_a_nameof_and_a_method_group_it_cannot_rewrite()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session,
			"Library.Shaped.Combine(string, string)",
			"string first, string separator, string second",
			["separator=\"-\""]);

		result.Applied.ShouldBeTrue("the change applies; the two sites it cannot rewrite are reported, not refused");

		var named = result.UnchangedCallSites
			.Where(site => site.Location.FilePath.EndsWith("Shaped.cs", StringComparison.OrdinalIgnoreCase))
			.ToArray();

		named.Length.ShouldBe(2);

		foreach (var site in named)
		{
			site.Reason.ShouldContain("names the member without calling it", Case.Sensitive);
		}

		// The delegate conversion is now wrong, and saying so is the whole point of reporting a site
		// nothing could be done about.
		result.IntroducedDiagnostics.Any(
			diagnostic => diagnostic.FilePath?.EndsWith("Shaped.cs", StringComparison.OrdinalIgnoreCase) == true).ShouldBeTrue();
	}

	/// <summary>
	/// A base-initialiser and a this-initialiser are calls, with arguments, that this leaves alone --
	/// the walk from a reference to its invocation climbs to the member declaration and never looks
	/// at a constructor initialiser on the way.
	/// <para>
	/// So they are refused, and the refusal says which shape it is. They used to arrive at the
	/// sentence written for a name that is not a call, which is false of them twice over: they call
	/// the constructor, and after this change they call it with too few arguments. The caller was
	/// pointed at the right lines by the wrong reason.
	/// </para>
	/// </summary>
	[Test]
	public async Task Says_a_constructor_initialiser_is_a_call_it_cannot_reach()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Rooted.Rooted(string)", "string name, int age", ["age=0"]);

		result.Applied.ShouldBeTrue("the initialisers are reported rather than refusing the whole change");

		var initialisers = result.UnchangedCallSites
			.Where(site => site.Location.FilePath.EndsWith("Shaped.cs", StringComparison.OrdinalIgnoreCase))
			.ToArray();

		initialisers.Length.ShouldBe(2);

		foreach (var site in initialisers)
		{
			site.Reason.ShouldContain("base or this initialiser", Case.Sensitive);
		}

		foreach (var site in initialisers)
		{
			site.Reason.ShouldNotContain("without calling it", Case.Sensitive);
		}

		// Both are real calls that now pass too few arguments, which is what makes the sentence above
		// the wrong one.
		result.IntroducedDiagnostics.Count(diagnostic => diagnostic.Id == "CS7036").ShouldBe(2);
	}

	/// <summary>
	/// An expression supplied for a new parameter is the caller's code, and it is written even where
	/// it does not resolve. The error is theirs to fix and the diagnostic is what points at it --
	/// reverting the whole change instead would leave them with neither the parameter nor the error.
	/// </summary>
	[Test]
	public async Task Writes_a_supplied_expression_that_does_not_resolve_and_reports_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Greeter.Greet(string)", "string name, bool loud", ["loud=NoSuchThing"]);

		result.Applied.ShouldBeTrue("the declaration and its call sites are written; the expression is the caller's");

		var text = await ReadAsync(fixture, "Caller.cs");

		text.ShouldContain("Greet(\"world\", NoSuchThing)", Case.Sensitive);

		result.IntroducedDiagnostics.Any(
			diagnostic => diagnostic.Id == "CS0103"
				&& diagnostic.FilePath?.EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase) == true).ShouldBeTrue();

		// The name does not resolve, which is a different thing from an argument on the wrong
		// parameter -- so nothing here is called a defect in the tool.
		result.Notices.ShouldNotContain(
			notice => notice.Contains("defect in rose_change_signature", StringComparison.Ordinal));
	}
}
