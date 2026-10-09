using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a write result says, and how little it takes to say it.
/// <para>
/// Without care a write result is mostly the caller's own code read back in a diff, the same absolute
/// path several times over, and notices that fire on every call. What the writer owns is where each
/// file changed and what it normalised, so that is what a result carries, each path named once relative
/// to the calling session's directory -- the directory a relative path sent back is measured from,
/// with a workspace key or without one.
/// </para>
/// <para>
/// Paths are composed from the running platform's root rather than written out, because this suite
/// runs on Linux as well, where a drive-letter path is a relative one.
/// </para>
/// </summary>
public sealed class WriteResultTests
{
	/// <summary>An edit that adds two lines and changes one names those lines as the file now reads.</summary>
	[Test]
	public void A_diff_names_the_lines_it_changed_as_the_file_now_reads()
	{
		var before = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n";
		var after = "a\nB\nc\nd\ne\nf\ng\nh\nnew\nnewer\ni\nj\n";

		var compared = UnifiedDiff.Compare("File.cs", before, after);

		compared.Lines.ShouldBe("2, 9-10");
		compared.Text.ShouldContain("+newer", Case.Sensitive);
	}

	/// <summary>A removal has no line of its own any more, so it names the line now standing where it was.</summary>
	[Test]
	public void A_removal_names_the_line_now_where_it_was()
	{
		UnifiedDiff.Compare("File.cs", "a\nb\nc\nd\n", "a\nb\nd\n").Lines.ShouldBe("3");
		UnifiedDiff.Compare("File.cs", "a\nb\nc\n", "a\nb\n").Lines.ShouldBe("2");
	}

	/// <summary>
	/// Changing only line endings changes no line's content, so there are no lines to name: the
	/// change is the file's normalised, which the writer reports beside it.
	/// </summary>
	[Test]
	public void A_change_of_endings_alone_names_no_lines()
	{
		UnifiedDiff.Compare("File.cs", "a\nb\n", "a\r\nb\r\n").Lines.ShouldBeNull();
	}

	/// <summary>A reformat that touches every other line names the first few ranges and counts the rest.</summary>
	[Test]
	public void Many_scattered_changes_are_counted_past_the_first_few()
	{
		var before = string.Join('\n', Enumerable.Range(0, 40).Select(line => $"line {line}")) + "\n";
		var after = string.Join('\n', Enumerable.Range(0, 40).Select(line => line % 4 == 0 ? $"LINE {line}" : $"line {line}")) + "\n";

		UnifiedDiff.Compare("File.cs", before, after).Lines.ShouldBe("1, 5, 9, 13, 17, 21 and 4 more");
	}

	[Test]
	public void A_new_file_names_every_line()
	{
		UnifiedDiff.NewFile("File.cs", "a\nb\nc\n").Lines.ShouldBe("1-3");
		UnifiedDiff.NewFile("File.cs", "a").Lines.ShouldBe("1");
		UnifiedDiff.NewFile("File.cs", string.Empty).Lines.ShouldBeNull();
	}

	/// <summary>
	/// The writer says where each file changed, and that it retyped endings -- beside the file it did it
	/// to, only where it did, since a terminator is not line content and no diff shows it.
	/// </summary>
	[Test]
	public async Task The_writer_names_each_file_with_its_lines_and_what_it_normalised()
	{
		using var workspace = new AdhocWorkspace();
		var project = workspace.AddProject("App", LanguageNames.CSharp);
		var retyped = DocumentId.CreateNewId(project.Id);
		var edited = DocumentId.CreateNewId(project.Id);
		var root = Absolute("checkouts", "main");

		var before = project.Solution
			.AddDocument(retyped, "Retyped.cs", "class A\n{\n}\n", filePath: Path.Combine(root, "Retyped.cs"))
			.AddDocument(edited, "Edited.cs", "class B\r\n{\r\n}\r\n", filePath: Path.Combine(root, "Edited.cs"));

		var after = before
			.WithDocumentText(retyped, SourceText.From("class A\r\n{\r\n}\r\n"))
			.WithDocumentText(edited, SourceText.From("class B\r\n{\r\n\tint x;\r\n}\r\n"))
			.AddDocument(DocumentId.CreateNewId(project.Id), "New.cs", "class C;\r\n", filePath: Path.Combine(root, "New.cs"));

		var outcome = await SolutionWriter.ApplyAsync(
			before, after, write: false, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken);

		var files = outcome.ChangedFiles.ToDictionary(file => Path.GetFileName(file.FilePath));

		files["Retyped.cs"].ShouldBe(new ChangedFile { FilePath = Path.Combine(root, "Retyped.cs"), Normalised = "3 line ending(s) to CRLF" });
		files["Edited.cs"].ShouldBe(new ChangedFile { FilePath = Path.Combine(root, "Edited.cs"), Lines = "3" });
		files["New.cs"].ShouldBe(new ChangedFile { FilePath = Path.Combine(root, "New.cs"), Lines = "1", Created = true });
		outcome.Notices.ShouldBeEmpty("what the writer did that a diff cannot show is a field of the file now");
	}

	/// <summary>A result about one file names it first, so the field that would have repeated it can go.</summary>
	[Test]
	public void The_file_a_result_is_about_leads()
	{
		var outcome = new WriteOutcome
		{
			ChangedFiles = [new ChangedFile { FilePath = "Other.cs" }, new ChangedFile { FilePath = "Mine.cs" }],
			Diff = string.Empty,
		};

		outcome.Leading("MINE.cs").Select(file => file.FilePath).ShouldBe(["Mine.cs", "Other.cs"]);
		outcome.Leading("Absent.cs").Select(file => file.FilePath).ShouldBe(["Other.cs", "Mine.cs"]);
	}

	/// <summary>A replacement of one member writes that member, which the symbol beside it already names.</summary>
	[Test]
	public void Members_are_left_off_where_they_only_repeat_the_symbol()
	{
		MemberEditService.MoreThanTheSymbol("Library.Greeter.Count", ["Count"]).ShouldBeNull();
		MemberEditService.MoreThanTheSymbol("TestToolchain.RunProcess(string, string)", ["RunProcess"]).ShouldBeNull();
		MemberEditService.MoreThanTheSymbol("Library.Box<T>.Open<TItem>(TItem)", ["Open"]).ShouldBeNull();
		MemberEditService.MoreThanTheSymbol("Library.Greeter", ["Count", "Name"]).ShouldBe(["Count", "Name"]);
		MemberEditService.MoreThanTheSymbol("Library.Greeter", ["Count"]).ShouldBe(["Count"]);
		MemberEditService.MoreThanTheSymbol("Library.Greeter.Count", []).ShouldBeNull();
	}

	/// <summary>
	/// Every path under the calling session's directory comes back relative to it, in every field that
	/// names one: the changed files, the diff's headers, the diagnostics and the notices.
	/// </summary>
	[Test]
	public void Paths_under_the_session_directory_are_named_relative_to_it()
	{
		var session = Absolute("checkouts", "main");
		var file = Path.Combine(session, "src", "Greeter.cs");
		var result = Edit(session, file) with
		{
			Diff = $"--- {file}\n+++ {file}\n@@ -1,1 +1,1 @@\n--- {file}\n+x\n",
			Notices = [$"{file}: lines 3-4 changed."],
			IntroducedDiagnostics =
			[
				new DiagnosticEntry { Id = "CS0103", Severity = "Error", Message = "m", Project = "App", FilePath = file, HelpLink = "https://example" },
			],
		};

		var relative = WritePaths.Relative(result, session);
		var named = Path.Combine("src", "Greeter.cs");

		relative.ChangedFiles.ShouldBe([new ChangedFile { FilePath = named, Lines = "3-4" }]);
		relative.Diff.ShouldBe($"--- {named}\n+++ {named}\n@@ -1,1 +1,1 @@\n--- {file}\n+x\n",
			"a removed line that happens to read as a header is content, and is left as it was");
		relative.Notices.ShouldBe([$"{named}: lines 3-4 changed."]);
		relative.IntroducedDiagnostics.ShouldHaveSingleItem().FilePath.ShouldBe(named);
		relative.IntroducedDiagnostics[0].HelpLink.ShouldBeNull();
		relative.Workspace.ShouldBe(result.Workspace, "the workspace is named once, absolutely");
	}

	/// <summary>
	/// What does not lie under the session's directory stays absolute rather than climbing out with
	/// <c>..</c>: a workspace the caller named in another checkout, a sibling whose name merely starts the
	/// same, and a generated document, whose path names nothing on disk.
	/// </summary>
	[Test]
	public void Paths_outside_the_session_directory_and_generated_documents_stay_absolute()
	{
		var session = Absolute("checkouts", "main");
		var otherCheckout = Absolute("checkouts", "worktree", "src", "Greeter.cs");
		var sibling = Absolute("checkouts", "main2", "Other.cs");
		var generated = Path.Combine(session, "obj", "Generator", "Hint.g.cs");

		var result = Edit(Absolute("checkouts", "worktree"), otherCheckout) with
		{
			ChangedFiles = [new ChangedFile { FilePath = otherCheckout }, new ChangedFile { FilePath = sibling }],
			Diff = $"--- {otherCheckout}\n+++ {otherCheckout}\n",
			IntroducedDiagnostics =
			[
				new DiagnosticEntry { Id = "CS0103", Severity = "Error", Message = "m", Project = "App", FilePath = generated, GeneratedHintName = "Hint.g.cs" },
			],
		};

		var relative = WritePaths.Relative(result, session);

		relative.ChangedFiles.Select(changed => changed.FilePath).ShouldBe([otherCheckout, sibling]);
		relative.Diff.ShouldBe(result.Diff);
		relative.IntroducedDiagnostics[0].FilePath.ShouldBe(generated);
	}

	/// <summary>
	/// A solution in a subfolder of the session's directory gives paths that start at the session's
	/// directory, not at the solution's, so they name the same file to the caller's own tools.
	/// </summary>
	[Test]
	public void A_solution_below_the_session_gives_paths_from_the_session()
	{
		var session = Absolute("repo");
		var file = Absolute("repo", "src", "App", "Foo.cs");

		var relative = WritePaths.Relative(Edit(Absolute("repo", "src"), file), session);

		relative.ChangedFiles.ShouldHaveSingleItem().FilePath.ShouldBe(Path.Combine("src", "App", "Foo.cs"));
	}

	/// <summary>
	/// Every write result type is one the relativising knows the fields of. A type it does not know would
	/// go out with its own path fields absolute, and nothing would say so.
	/// </summary>
	[Test]
	public void Every_write_result_type_has_its_paths_named_relative()
	{
		var writes = typeof(WorkspaceMutationResult).Assembly.GetTypes()
			.Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(WorkspaceMutationResult)));

		WritePaths.Handled.ShouldBe(writes, ignoreOrder: true);
	}

	/// <summary>
	/// The round trip, from the argument's side: a relative path is measured from the session's
	/// directory whether or not the call names its workspace by key, which is the directory a result
	/// named it from, so a key never changes which file a relative path means. Measured from the key's
	/// workspace instead, <c>src/App/Foo.cs</c> sent from <c>repo</c> about a solution in <c>repo/src</c>
	/// would mean <c>repo/src/src/App/Foo.cs</c>.
	/// </summary>
	[Test]
	public void A_relative_path_is_measured_from_the_session_with_or_without_a_key()
	{
		var paths = new CallerPaths(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = Absolute("broker") }));

		using var session = CallOrigin.Use(Absolute("repo"));

		(paths.Of(Path.Combine("src", "App", "Foo.cs"))?.Value).ShouldBe(Absolute("repo", "src", "App", "Foo.cs"));
		(paths.Of(Absolute("elsewhere", "Foo.cs"))?.Value).ShouldBe(Absolute("elsewhere", "Foo.cs"));
		paths.Origin.ShouldBe(Absolute("repo"));
	}

	/// <summary>
	/// An applied write leaves its diff off unless asked: nearly all of it is the code the caller sent.
	/// A preview keeps it, because a preview is the diff.
	/// </summary>
	[Test]
	public void An_applied_write_leaves_its_diff_off_unless_asked()
	{
		var applied = Edit(Absolute("main"), Absolute("main", "A.cs")) with { Diff = "--- a\n+++ a\n" };

		WriteForCaller.Shape(applied, includeDiff: false).Diff.ShouldBeNull();
		WriteForCaller.Shape(applied, includeDiff: true).Diff.ShouldBe("--- a\n+++ a\n");
		WriteForCaller.Shape(applied with { Applied = false }, includeDiff: false).Diff.ShouldBe("--- a\n+++ a\n");
		WriteForCaller.Shape(applied with { Applied = false, Diff = string.Empty }, includeDiff: false).Diff.ShouldBeNull();
		WriteForCaller.Shape(applied, includeDiff: false).Notices.ShouldBeEmpty("leaving off what was not asked for is not news");
	}

	/// <summary>
	/// A diff past what a client accepts is left out with a notice, asked for or not, and the notice
	/// sends the caller only somewhere the diff still is: a narrower preview where there is one to ask
	/// for, git for a write already on disk, and the file itself for one the write created, which git
	/// diff leaves out as untracked. A preview of one file is too large to carry and cannot be narrowed,
	/// so it is told no more than that.
	/// </summary>
	[Test]
	public void A_diff_past_the_ceiling_is_left_out_and_says_where_to_read_it()
	{
		var size = $"The diff is {WriteForCaller.DiffCeiling + 1:N0} characters, past the {WriteForCaller.DiffCeiling:N0} a result "
			+ "carries, and was left out: ";
		var preview = Edit(Absolute("main"), Absolute("main", "A.cs")) with
		{
			Applied = false,
			Diff = new string('+', WriteForCaller.DiffCeiling + 1),
		};

		var previewed = WriteForCaller.Shape(preview, includeDiff: true);

		previewed.Diff.ShouldBeNull();
		previewed.Notices.ShouldHaveSingleItem().ShouldBe(size + "changedFiles gives its changed lines.");

		var two = preview with
		{
			ChangedFiles = [new ChangedFile { FilePath = "A.cs", Lines = "1" }, new ChangedFile { FilePath = "B.cs", Lines = "2" }],
		};

		WriteForCaller.Shape(two, includeDiff: true).Notices.ShouldHaveSingleItem().ShouldBe(
			size + "a preview over fewer files returns it, and changedFiles gives each file's changed lines.");

		WriteForCaller.Shape(preview with { Applied = true }, includeDiff: true).Notices.ShouldHaveSingleItem().ShouldBe(
			size + "the write is done, so git diff shows it, and changedFiles gives its changed lines.");

		var added = preview with
		{
			Applied = true,
			ChangedFiles = [new ChangedFile { FilePath = "New.cs", Lines = "1-900", Created = true }],
		};

		WriteForCaller.Shape(added, includeDiff: true).Notices.ShouldHaveSingleItem().ShouldBe(
			size + "the write is done, and git diff leaves out New.cs, which it created and git does not track, so read it, "
				+ "and changedFiles gives its changed lines.");

		var moved = added with
		{
			ChangedFiles = [new ChangedFile { FilePath = "Old.cs", Lines = "3-40" }, .. added.ChangedFiles],
		};

		WriteForCaller.Shape(moved, includeDiff: true).Notices.ShouldHaveSingleItem().ShouldBe(
			size + "the write is done, so git diff shows its changes to files that existed, but not New.cs, which it created "
				+ "and git does not track, so read it, and changedFiles gives each file's changed lines.");
	}

	/// <summary>
	/// Where the changed files were cut too, the diff notice does not claim their lines cover every file.
	/// </summary>
	[Test]
	public void A_cut_list_is_not_claimed_to_cover_every_file()
	{
		var many = Enumerable.Range(0, WriteForCaller.ChangedFileRows + 5)
			.Select(index => new ChangedFile { FilePath = Absolute("main", $"F{index}.cs"), Lines = "1" })
			.ToList();
		var written = Edit(Absolute("main"), Absolute("main", "A.cs")) with
		{
			ChangedFiles = many,
			Diff = new string('+', WriteForCaller.DiffCeiling + 1),
		};

		var shaped = WriteForCaller.Shape(written, includeDiff: true);

		shaped.ChangedFiles.Count.ShouldBe(WriteForCaller.ChangedFileRows);
		shaped.Notices.ShouldBe([
			$"The diff is {WriteForCaller.DiffCeiling + 1:N0} characters, past the {WriteForCaller.DiffCeiling:N0} a result "
				+ "carries, and was left out: the write is done, so git diff shows it, and changedFiles gives the changed "
				+ $"lines of the first {WriteForCaller.ChangedFileRows} files only.",
			$"changedFiles names {WriteForCaller.ChangedFileRows} of the {WriteForCaller.ChangedFileRows + 5} files this wrote.",
		]);
	}

	/// <summary>
	/// An http call that did not say where it comes from has no session directory the broker knows, so its
	/// paths stay absolute -- relative to the tray's directory they would name a file the caller's own tools
	/// cannot find -- while a stdio host's own directory is its one client's, and counts.
	/// </summary>
	[Test]
	public void A_call_from_an_unknown_directory_names_its_paths_absolutely()
	{
		var tray = Absolute("tray");
		var file = Absolute("tray", "src", "Greeter.cs");
		var http = new CallerPaths(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = tray }));
		var stdio = new CallerPaths(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = tray, DefaultRootIsTheCaller = true }));

		http.KnownOrigin.ShouldBeNull();
		stdio.KnownOrigin.ShouldBe(tray);

		var result = Edit(tray, file) with
		{
			IntroducedDiagnostics =
			[
				new DiagnosticEntry { Id = "CS0103", Severity = "Error", Message = "m", Project = "App", FilePath = file, HelpLink = "https://example" },
			],
		};

		var shown = WritePaths.Relative(result, http.KnownOrigin);

		shown.ChangedFiles.ShouldHaveSingleItem().FilePath.ShouldBe(file);
		shown.IntroducedDiagnostics[0].FilePath.ShouldBe(file);
		shown.IntroducedDiagnostics[0].HelpLink.ShouldBeNull("a help link is noise wherever the call came from");

		using var relayed = CallOrigin.Use(Absolute("tray", "src"));

		http.KnownOrigin.ShouldBe(Absolute("tray", "src"), "a relayed call says where it comes from");
	}

	/// <summary>
	/// The errors already there are a count, and the one thing the count cannot say is that part of it is
	/// analyzer errors, which rose_diagnostics leaves out unless asked. So that is said where it is true and
	/// only there; a count of compiler errors alone needs no sentence.
	/// </summary>
	[Test]
	public void Says_how_many_errors_already_there_are_analyzer_errors_only_where_any_are()
	{
		new RoseMcp.Worker.Verification { Ran = true, PreexistingCount = 3 }.PreexistingAdvice().ShouldBeNull();
		new RoseMcp.Worker.Verification { Ran = true }.PreexistingAdvice().ShouldBeNull();

		new RoseMcp.Worker.Verification { Ran = true, PreexistingCount = 3, PreexistingAnalyzerCount = 1 }.PreexistingAdvice().ShouldBe(
			"1 of the 3 errors already there is an analyzer error, which rose_diagnostics leaves out unless includeAnalyzers=true.");
		new RoseMcp.Worker.Verification { Ran = true, PreexistingCount = 297, PreexistingAnalyzerCount = 297 }.PreexistingAdvice().ShouldBe(
			"297 of the 297 errors already there are analyzer errors, which rose_diagnostics leaves out unless includeAnalyzers=true.");
	}

	private static MemberEditResult Edit(string root, string file) => new()
	{
		Workspace = Path.Combine(root, "App.slnx"),
		WorkspaceKey = "App-00000000",
		Revision = 1,
		Symbol = "Library.Greeter.Count",
		Line = 3,
		Applied = true,
		Verified = true,
		ChangedFiles = [new ChangedFile { FilePath = file, Lines = "3-4" }],
	};

	/// <summary>An absolute path on whichever platform is running: a drive root here, / elsewhere.</summary>
	private static string Absolute(params string[] parts) =>
		Path.GetFullPath(Path.Combine([Path.GetPathRoot(AppContext.BaseDirectory)!, .. parts]));
}
