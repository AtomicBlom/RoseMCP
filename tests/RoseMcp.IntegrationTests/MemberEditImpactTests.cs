using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.MemberEdits;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The other half of the promise: a good call says what it broke, without a build. An edit is
/// compiled against its dependents and the errors are reported as the edit's own, so a caller learns
/// what a reshaped public member cost at the call sites rather than at the next build.
/// <para>
/// Scope is the thing these pin down. A private member cannot break another project and a body change
/// cannot change a signature, so neither pays for compiling the whole graph -- and where the scope was
/// narrowed, the dependents that were skipped are named rather than silently dropped.
/// </para>
/// </summary>
public sealed class MemberEditImpactTests
{
	/// <summary>
	/// The compilation happens in the same call, which is the whole reason this is not two. A body
	/// that does not compile comes back as an error against the member rather than as a build twenty
	/// seconds later.
	/// </summary>
	[Test]
	public async Task Says_what_the_edit_broke_without_a_build()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var good = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => $\"{_prefix}, {name}.\";");

		good.Verified.ShouldBeTrue();
		good.IntroducedDiagnostics.ShouldBeEmpty();
		good.PreexistingErrorCount.ShouldBe(0);
		good.ProjectsChecked.ShouldContain("Library");

		var bad = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name) => _prefix.Missing(name);");

		var introduced = bad.IntroducedDiagnostics.ShouldHaveSingleItem();

		introduced.Id.ShouldBe("CS1061");
		introduced.FilePath.ShouldEndWith("Greeter.cs", Case.Insensitive);
		bad.PreexistingErrorCount.ShouldBe(0, "the one error there is the one this edit introduced");
	}

	/// <summary>
	/// The failure this tool was built for. A signature change breaks its call sites, those are in
	/// other files, and the one that reached a build undetected in the session behind all of this
	/// was found only from CS7036 -- so the answer names it, in a file the edit never touched.
	/// </summary>
	[Test]
	public async Task Reports_the_call_site_a_changed_signature_breaks()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await ReplaceAsync(
			session,
			"Library.Greeter.Greet(string)",
			"public string Greet(string name, bool loud) => loud ? name.ToUpperInvariant() : name;");

		result.Applied.ShouldBeTrue();

		var introduced = result.IntroducedDiagnostics.ShouldHaveSingleItem();

		introduced.Id.ShouldBe("CS1501");
		introduced.FilePath.ShouldEndWith("Caller.cs", Case.Insensitive);

		// And the answer says how far it looked: the scope took in every dependent, so none went
		// unchecked, and no sentence claims otherwise beside the empty list.
		result.ProjectsChecked.ShouldNotBeEmpty();
		result.DependentsNotChecked.ShouldBeEmpty();
		result.Notices.ShouldNotContain(notice => notice.Contains("dependents", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public async Task Writes_nothing_when_previewing_and_still_says_what_it_would_break()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Greeter.Greet(string)",
			Code = "public string Greet(string name, bool loud) => name;",
			Apply = false,
		});

		result.Applied.ShouldBeFalse("a preview writes nothing");
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);

		// The diff and the breakage are the point of asking: both describe a change that did not happen.
		result.Diff.ShouldNotBeNull().ShouldContain("bool loud", Case.Sensitive);
		result.IntroducedDiagnostics.ShouldContain(diagnostic => diagnostic.Id == "CS1501");
	}

	/// <summary>
	/// An unverified edit has to say so. An empty introduced list means nothing at all when nothing
	/// was compiled, and reads exactly like a clean result -- so <c>verified</c> says it, once, and no
	/// sentence repeats the argument the caller passed.
	/// </summary>
	[Test]
	public async Task Says_when_it_did_not_compile_anything()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Library.Greeter.Greet(string)",
			Code = "public string Greet(string name) => name.Missing();",
			Verify = false,
		});

		result.Applied.ShouldBeTrue();
		result.Verified.ShouldBeFalse("verify=false compiles nothing, and says so");
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldNotContain(notice => notice.Contains("compiled", StringComparison.Ordinal), "verified is the fact, and verify=false is what the caller sent");
	}

	/// <summary>
	/// The errors that were already there are a count of their own, separate from what this edit
	/// introduced, and said once: as a field rather than again in a sentence beside it.
	/// </summary>
	[Test]
	public async Task Counts_the_errors_that_were_already_there()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		// One error to be pre-existing by the time the second edit runs.
		var broken = await EditAsync(session, Request(
			MemberEditKind.Add,
			"Library.Prose",
			"public static string Missing() => Absent.Name;"));

		broken.IntroducedDiagnostics.ShouldNotBeEmpty();

		var result = await ReplaceAsync(
			session,
			"Library.Prose.Label",
			"public static string Label()\n{\n\treturn \"count\";\n}");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		result.PreexistingErrorCount.ShouldBeGreaterThan(0, "the earlier edit left an error behind");
		result.Notices.ShouldNotContain(notice => notice.Contains("analyzer error", StringComparison.Ordinal),
			"the error already there is the compiler's, which rose_diagnostics reports by default");
		result.Notices.ShouldNotContain(notice => notice.Contains("compiles clean", StringComparison.Ordinal));
		result.Notices.ShouldNotContain(notice => notice.Contains("error(s)", StringComparison.Ordinal));
	}

	/// <summary>
	/// A public member reshaped breaks its dependents by construction, so the projects that reference
	/// this one are compiled too. Checking only the file's own would report a clean edit at exactly the
	/// moment it is not one -- which is the confident-answer-to-a-different-question this whole surface
	/// is built to avoid.
	/// </summary>
	[Test]
	public async Task Compiles_the_dependents_of_a_public_member()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Multiply",
			Code = "public static int Multiply(int left, int right, int scale) => left * right * scale;",
		});

		result.ProjectsChecked.ShouldContain("App");
		result.IntroducedDiagnostics.ShouldContain(
			entry => entry.FilePath!.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
		result.DependentsNotChecked.ShouldBeEmpty();
	}

	/// <summary>
	/// A repository that escalates a style rule to an error fails its build on a diagnostic no compiler
	/// pass produces. Verifying without analyzers therefore reports clean on an edit that does not
	/// build, which breaks the one promise a caller cannot check without the build this exists to
	/// replace: that the result names the errors the edit introduced.
	/// <para>
	/// The copy stands in for such a repository. Turning IDE0005 up in the checked-in fixture instead
	/// would put an unused import between every other test here and its assertion.
	/// </para>
	/// </summary>
	[Test]
	public async Task Reports_an_analyzer_error_the_edit_introduced()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");

		await File.AppendAllTextAsync(
			fixture.Path("Members", ".editorconfig"),
			Environment.NewLine + "dotnet_diagnostic.IDE0005.severity = error" + Environment.NewLine,
			TestContext.Current!.Execution.CancellationToken);

		// Both properties are needed: the first is what puts the code-style analyzers in front of the
		// compiler at all, and IDE0005 stays quiet without the second, since a using directive can be
		// needed by a documentation comment alone.
		var project = fixture.Path("Members", "Library", "Library.csproj");
		var projectText = await File.ReadAllTextAsync(project, TestContext.Current!.Execution.CancellationToken);
		await File.WriteAllTextAsync(
			project,
			projectText.Replace(
				"<ImplicitUsings>enable</ImplicitUsings>",
				"<ImplicitUsings>enable</ImplicitUsings>"
					+ Environment.NewLine + "    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>"
					+ Environment.NewLine + "    <GenerateDocumentationFile>true</GenerateDocumentationFile>"),
			TestContext.Current!.Execution.CancellationToken);

		await using var session = await TestSession.OpenAsync(fixture);

		// Formatted holds the file's only use of System.Globalization, so a body without CultureInfo
		// leaves the import unused and the project no longer builds.
		var result = await ReplaceAsync(
			session,
			"Library.Imports.Formatted(double)",
			"public static string Formatted(double value) => value.ToString();");

		result.Applied.ShouldBeTrue();

		result.IntroducedDiagnostics.ShouldContain(entry => entry.Id == "IDE0005");

		// The next edit in the same project finds that error already there. The count includes it, and
		// rose_diagnostics leaves analyzers out by default, so the result says so -- otherwise its count
		// beside that tool's zero reads as the two disagreeing.
		var next = await ReplaceAsync(
			session,
			"Library.Prose.Label",
			"public static string Label()\n{\n\treturn \"counted\";\n}");

		next.IntroducedDiagnostics.ShouldBeEmpty();
		next.PreexistingErrorCount.ShouldBeGreaterThan(0);
		next.Notices.ShouldContain(
			notice => notice.Contains("analyzer error", StringComparison.Ordinal)
				&& notice.Contains("includeAnalyzers=true", StringComparison.Ordinal),
			string.Join(" | ", next.Notices));
	}

	/// <summary>
	/// Narrowing the scope by hand is allowed and is not silent: the same edit reports nothing wrong,
	/// and says which dependents nobody looked at. Reporting no introduced errors without that is a
	/// clean bill of health for half the question.
	/// </summary>
	[Test]
	public async Task Names_the_dependents_a_narrowed_scope_skipped()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Multiply",
			Code = "public static int Multiply(int left, int right, int scale) => left * right * scale;",
			VerifyScope = VerifyScope.File,
		});

		result.ProjectsChecked.ShouldNotContain("App");
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.DependentsNotChecked.ShouldContain("App");
		result.Notices.ShouldContain(notice => notice.StartsWith("dependentsNotChecked can see what this changed", StringComparison.Ordinal));
	}

	/// <summary>
	/// The warning about unchecked dependents is said only where there are some. A replacement whose
	/// scope already took in every dependent, which auto always does, has none, and a sentence saying
	/// they went unchecked would contradict the empty list beside it.
	/// </summary>
	[Test]
	public async Task Says_nothing_about_dependents_when_none_went_unchecked()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Multiply",
			Code = "public static int Multiply(int left, int right) => right * left;",
		});

		result.Applied.ShouldBeTrue();
		result.ProjectsChecked.ShouldContain("App");
		result.DependentsNotChecked.ShouldBeEmpty();
		result.Notices.ShouldNotContain(notice => notice.Contains("dependents", StringComparison.OrdinalIgnoreCase));
		result.Notices.ShouldNotContain(notice => notice.StartsWith("Only ", StringComparison.Ordinal));
	}

	/// <summary>
	/// A private member cannot be seen outside the projects holding it however the edit reshapes it,
	/// so the wide scope is not paid for. Effective accessibility, not declared: a public member of a
	/// private nested type is private too.
	/// </summary>
	[Test]
	public async Task Leaves_a_private_member_in_its_own_projects()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Replace,
			Symbol = "Core.Calculator.Twice",
			Code = "private static int Twice(int value, int times) => value * times;",
		});

		result.ProjectsChecked.ShouldBe(["Core"]);
		result.DependentsNotChecked.ShouldBeEmpty();
	}

	/// <summary>
	/// A body cannot be seen outside at all: the signature that comes out is the one that was there,
	/// copied rather than rewritten, so nothing downstream can be looking at anything different.
	/// </summary>
	[Test]
	public async Task Leaves_a_body_change_in_its_own_projects()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.ReplaceBody,
			Symbol = "Core.Calculator.Multiply",
			Code = "=> right * left;",
		});

		result.ProjectsChecked.ShouldBe(["Core"]);
	}

	/// <summary>
	/// Removing something still referenced is allowed -- the callers may be going too -- and the call
	/// sites come back as the errors it introduced rather than at the next build.
	/// </summary>
	[Test]
	public async Task Reports_what_a_removal_broke_across_the_dependents()
	{
		using var fixture = FixtureSolution.Copy("Simple", "Simple.sln");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await EditAsync(session, new MemberEditRequest
		{
			Kind = MemberEditKind.Delete,
			Symbol = "Core.Calculator.Multiply",
		});

		result.Applied.ShouldBeTrue();
		result.ProjectsChecked.ShouldContain("App");
		result.IntroducedDiagnostics.ShouldContain(
			entry => entry.FilePath!.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
		result.DependentsNotChecked.ShouldBeEmpty();
	}
}
