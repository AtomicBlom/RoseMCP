using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// The writer's refusal to resolve a relative document path. A <see cref="Document"/> whose
/// <see cref="TextDocument.FilePath"/> is a bare name would be written against the worker process's
/// current directory -- the repository root, ordinarily -- while the result named a path that reads
/// as though it went to the project. Refusing is loud where writing it is silent.
/// </summary>
public sealed class SolutionWriterTests
{
	[Test]
	public async Task Refuses_to_write_an_added_document_whose_path_is_relative()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: "Added.cs");

		var failure = await Should.ThrowAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Message.ShouldContain("Added.cs", Case.Sensitive);
		failure.Message.ShouldContain("relative path", Case.Sensitive);
	}

	[Test]
	public async Task Refuses_to_write_a_changed_document_whose_path_is_relative()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var documentId = DocumentId.CreateNewId(project.Id);
		var before = project.Solution.AddDocument(documentId, "Existing.cs", "class Existing;", filePath: "Existing.cs");

		var after = before.WithDocumentText(documentId, SourceText.From("class Existing { }"));

		var failure = await Should.ThrowAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken)).OfExactType();

		failure.Message.ShouldContain("Existing.cs", Case.Sensitive);
	}

	/// <summary>
	/// The refusal is not a write-time check: a preview asks the same question, because a diff
	/// naming a path nobody can write is as misleading as the write itself.
	/// </summary>
	[Test]
	public async Task Refuses_a_relative_path_even_when_not_writing()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: "Added.cs");

		await Should.ThrowAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: false, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken)).OfExactType();
	}

	[Test]
	public async Task Renders_a_rooted_added_document_without_writing_it()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;
		var path = Path.Combine(Path.GetTempPath(), "RoseMcpSolutionWriterTests", "Added.cs");

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: path);

		var outcome = await SolutionWriter.ApplyAsync(before, after, write: false, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken);

		outcome.ChangedFiles.ShouldBe([path]);
		File.Exists(path).ShouldBeFalse("rendering the diff writes nothing");
	}

	/// <summary>
	/// A multi-targeted project loads once per target framework, so an edit to one of its files is a
	/// change in two projects. It is one file with one diff, and listing it twice reads as the same edit
	/// made twice.
	/// </summary>
	[Test]
	public async Task Reports_a_file_shared_by_two_target_frameworks_once()
	{
		using var workspace = new AdhocWorkspace();
		var path = Path.Combine(Path.GetTempPath(), "RoseMcpSolutionWriterTests", "Shared.cs");

		var android = workspace.AddProject("App(net10.0-android)", LanguageNames.CSharp);
		var androidDocument = DocumentId.CreateNewId(android.Id);
		var before = android.Solution.AddDocument(androidDocument, "Shared.cs", "class Shared;", filePath: path);

		var desktop = before.AddProject("App(net10.0-desktop)", "App", LanguageNames.CSharp);
		var desktopDocument = DocumentId.CreateNewId(desktop.Id);
		before = desktop.Solution.AddDocument(desktopDocument, "Shared.cs", "class Shared;", filePath: path);

		var edited = SourceText.From("class Shared { }");
		var after = before.WithDocumentText(androidDocument, edited).WithDocumentText(desktopDocument, edited);

		var outcome = await SolutionWriter.ApplyAsync(
			before, after, write: false, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken);

		outcome.ChangedFiles.ShouldBe([path]);
		CountOf(outcome.Diff, "+++ ").ShouldBe(1);
	}

	private static int CountOf(string text, string value) =>
		(text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

	/// <summary>
	/// A byte order mark is part of the file. Rewriting the file through an API that has its own
	/// idea of the encoding takes the mark off every file that had one -- three bytes no diff can
	/// show, on a change that promised to touch one member.
	/// </summary>
	[Test]
	public async Task Keeps_the_byte_order_mark_a_file_already_had()
	{
		using var directory = new TempDirectory();
		var path = directory.PathTo("Marked.cs");

		await WriteAndReadBackAsync(path, Encoding.UTF8, "class Marked { }");

		StartsWithMark(path).ShouldBeTrue("the file arrived with a mark and has to keep it");
		(await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken)).ShouldBe("class Marked { }");
	}

	/// <summary>
	/// The other half of the same rule, and the one a careless fix breaks: a file with no mark must
	/// not acquire one, which is what writing everything as <see cref="Encoding.UTF8"/> would do.
	/// </summary>
	[Test]
	public async Task Puts_no_byte_order_mark_on_a_file_that_had_none()
	{
		using var directory = new TempDirectory();
		var path = directory.PathTo("Bare.cs");

		await WriteAndReadBackAsync(path, SourceEncoding.Utf8WithoutMark, "class Bare { }");

		StartsWithMark(path).ShouldBeFalse("nothing gave this file a mark to keep");
	}

	/// <summary>
	/// A file being created has no encoding to inherit, and gets the one <c>charset = utf-8</c>
	/// names rather than a mark nobody asked for.
	/// </summary>
	[Test]
	public async Task Writes_a_new_file_without_a_byte_order_mark()
	{
		using var workspace = new AdhocWorkspace();
		using var directory = new TempDirectory();

		var project = Empty(workspace);
		var before = project.Solution;
		var path = directory.PathTo("Added.cs");

		var after = before.AddDocument(
			DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: path);

		await SolutionWriter.ApplyAsync(
			before, after, write: true, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken);

		StartsWithMark(path).ShouldBeFalse();
	}

	/// <summary>
	/// Stages <paramref name="path"/> in <paramref name="encoding"/>, loads it the way the disk
	/// barrier does, edits it and writes it back -- the whole round trip the mark has to survive.
	/// </summary>
	private static async Task WriteAndReadBackAsync(string path, Encoding encoding, string edited)
	{
		var token = TestContext.Current!.Execution.CancellationToken;

		await File.WriteAllTextAsync(path, "class C { }", encoding, token);

		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var documentId = DocumentId.CreateNewId(project.Id);

		var loaded = Read(path);

		var before = project.Solution.AddDocument(documentId, "C.cs", loaded, filePath: path);
		var after = before.WithDocumentText(documentId, SourceText.From(edited, loaded.Encoding));

		await SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, token);
	}

	/// <summary>The file as the disk barrier reads it, closed again before anything writes to it.</summary>
	private static SourceText Read(string path)
	{
		using var stream = File.OpenRead(path);

		return SourceText.From(stream, SourceEncoding.Utf8WithoutMark, canBeEmbedded: false);
	}

	private static bool StartsWithMark(string path) =>
		File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble());

	private sealed class TempDirectory : IDisposable
	{
		private readonly string _root = Directory.CreateTempSubdirectory("rosemcp-writer-").FullName;

		public string PathTo(string name) => Path.Combine(_root, name);

		public void Dispose() => Directory.Delete(_root, recursive: true);
	}

	private static Project Empty(AdhocWorkspace workspace) =>
		workspace.AddProject("Sample", LanguageNames.CSharp);
}
