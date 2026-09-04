using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

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
	[Fact]
	public async Task Adds_an_optional_parameter_and_reports_the_call_sites_it_left()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Greeter.Greet(string)", "string name, bool loud = false");

		Assert.True(result.Applied);
		Assert.True(result.Verified);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(0, result.TotalErrorCount);

		var text = await ReadAsync(fixture, "Greeter.cs");

		Assert.Contains("public string Greet(string name, bool loud = false)", text, StringComparison.Ordinal);

		// Caller.cs calls it and needed nothing, and is named anyway.
		Assert.Contains(
			result.UnchangedCallSites,
			site => site.Location.FilePath.EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase)
				&& site.Reason.Contains("every new parameter has a default", StringComparison.Ordinal));
	}

	/// <summary>
	/// A required parameter, which every call site does have to change. The argument is written as
	/// a named one, because that is valid wherever it lands and needs no reasoning about position.
	/// </summary>
	[Fact]
	public async Task Adds_a_required_parameter_and_passes_it_at_every_call_site()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Greeter.Greet(string)", "string name, bool loud", ["loud=false"]);

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);

		var caller = await ReadAsync(fixture, "Caller.cs");

		// Positionally, because it lands in its own slot: a named argument is only needed once
		// something before it has been omitted or moved.
		Assert.Contains("Greet(\"world\", false)", caller, StringComparison.Ordinal);
		Assert.Single(result.UpdatedCallSites);
	}

	/// <summary>
	/// Refused before anything is written. A required parameter with nothing to pass would break
	/// every call site, and which of the two the caller meant is not something to guess at.
	/// </summary>
	[Fact]
	public async Task Refuses_a_required_parameter_with_nothing_to_pass()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Greet(string)", "string name, bool loud"));

		Assert.Contains("loud would be required", error.Message, StringComparison.Ordinal);
		Assert.Contains("name=expression", error.Message, StringComparison.Ordinal);
		Assert.Equal(before, await ReadAsync(fixture, "Greeter.cs"));
	}

	/// <summary>
	/// The declarations that have to move together. Changing only the one named does not compile,
	/// and the override calls its parameter something else -- so its own name has to survive, or
	/// the change would rename it without saying so.
	/// </summary>
	[Fact]
	public async Task Moves_the_interface_the_base_and_the_override_together()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Notifier.Notify(string)", "string message, bool urgent = false");

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(0, result.TotalErrorCount);

		var text = await ReadAsync(fixture, "Layers.cs");

		// The interface, the base, and the override -- which keeps calling its parameter text.
		Assert.Contains("string Notify(string message, bool urgent = false);", text, StringComparison.Ordinal);
		Assert.Contains("public virtual string Notify(string message, bool urgent = false)", text, StringComparison.Ordinal);
		Assert.Contains("public override string Notify(string text, bool urgent = false)", text, StringComparison.Ordinal);

		Assert.Equal(3, result.UpdatedDeclarations.Count);
	}

	/// <summary>
	/// Where documentation is generated, a tag for a parameter that no longer exists is CS1572 and
	/// a parameter with no tag is CS1573 -- both errors in a repository that treats warnings as
	/// errors. So a change that left the tags alone would compile the code and break the build.
	/// </summary>
	[Fact]
	public async Task Keeps_the_param_tags_in_step()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session, "Library.Notifier.Notify(string)", "string message, bool urgent = false");

		var text = await ReadAsync(fixture, "Layers.cs");

		Assert.Contains("<param name=\"urgent\"></param>", text, StringComparison.Ordinal);
		Assert.Contains("<param name=\"message\">What to say.</param>", text, StringComparison.Ordinal);
		Assert.NotEmpty(result.DocumentationUpdated);

		// And it says the tag it added has no description, which is not something to invent.
		Assert.Contains(
			result.Notices,
			notice => notice.Contains("needs a description", StringComparison.Ordinal));
	}

	/// <summary>
	/// Removing a parameter takes its argument and its tag with it -- and reports the bodies that
	/// were using it, which is a thing no tool can fix and the caller has to decide about. The
	/// value is that it is one answer rather than a build, and it names both places.
	/// </summary>
	[Fact]
	public async Task Removes_a_parameter_and_reports_the_bodies_that_used_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Notifier.Notify(string)", string.Empty);

		Assert.True(result.Applied);

		var text = await ReadAsync(fixture, "Layers.cs");

		// The declarations, the tag and the arguments all went.
		Assert.Contains("public virtual string Notify()", text, StringComparison.Ordinal);
		Assert.Contains("public override string Notify()", text, StringComparison.Ordinal);
		Assert.Contains("string Notify();", text, StringComparison.Ordinal);
		Assert.DoesNotContain("<param name=\"message\">", text, StringComparison.Ordinal);
		Assert.Contains("notifier.Notify()", text, StringComparison.Ordinal);

		// And the two bodies that still refer to the parameter are named, in one answer.
		Assert.Equal(2, result.IntroducedDiagnostics.Count);
		Assert.All(result.IntroducedDiagnostics, entry => Assert.Equal("CS0103", entry.Id));
	}

	/// <summary>
	/// Reordering what is already there is refused, because an argument's meaning at a call site is
	/// not always recoverable from its position. Inserting a new parameter in the middle is not the
	/// same thing and is allowed.
	/// </summary>
	[Fact]
	public async Task Refuses_to_reorder_parameters_that_already_exist()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Greet(string, string)", "string name, string title"));

		Assert.Contains("would move in front of", error.Message, StringComparison.Ordinal);
		Assert.Contains("New parameters can go anywhere", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A new parameter in the middle: the arguments after it can no longer be positional, so they
	/// are written as named ones rather than left to bind to the wrong parameter.
	/// </summary>
	[Fact]
	public async Task Names_the_arguments_a_new_parameter_displaces()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(
			session,
			"Library.Greeter.Greet(string, string)",
			"string title, bool loud, string name",
			["loud=true"]);

		Assert.True(result.Applied);
		Assert.Empty(result.IntroducedDiagnostics);
		Assert.Equal(0, result.TotalErrorCount);
	}

	/// <summary>
	/// Retyping changes nothing at the call sites, which is exactly why it is worth a warning: an
	/// argument that still converts will compile and mean something else.
	/// </summary>
	[Fact]
	public async Task Says_when_a_parameter_changed_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ChangeAsync(session, "Library.Greeter.Greet(string)", "object name");

		Assert.Contains(
			result.Notices,
			notice => notice.Contains("Retyped name", StringComparison.Ordinal));
	}

	[Fact]
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
			TestContext.Current.CancellationToken);

		Assert.False(result.Applied);
		Assert.Equal(before, await ReadAsync(fixture, "Layers.cs"));
		Assert.Contains("Preview only", string.Join(" ", result.Notices), StringComparison.Ordinal);
		Assert.Contains("urgent", result.Diff, StringComparison.Ordinal);
	}

	/// <summary>A member with no parameter list to change is told so rather than mangled.</summary>
	[Fact]
	public async Task Declines_what_has_no_parameters()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var error = await Assert.ThrowsAsync<ArgumentException>(
			() => ChangeAsync(session, "Library.Greeter.Count", "int value"));

		Assert.Contains("no parameter list", error.Message, StringComparison.Ordinal);
		Assert.Contains("rose_replace_member", error.Message, StringComparison.Ordinal);
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
			TestContext.Current.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, string file) =>
		File.ReadAllTextAsync(fixture.Path("Members", "Library", file), TestContext.Current.CancellationToken);
}
