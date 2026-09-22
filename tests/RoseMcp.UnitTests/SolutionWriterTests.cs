using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("Added.cs", failure.Message, StringComparison.Ordinal);
		Assert.Contains("relative path", failure.Message, StringComparison.Ordinal);
	}

	[Test]
	public async Task Refuses_to_write_a_changed_document_whose_path_is_relative()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var documentId = DocumentId.CreateNewId(project.Id);
		var before = project.Solution.AddDocument(documentId, "Existing.cs", "class Existing;", filePath: "Existing.cs");

		var after = before.WithDocumentText(documentId, SourceText.From("class Existing { }"));

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken));

		Assert.Contains("Existing.cs", failure.Message, StringComparison.Ordinal);
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

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: false, noteSelfWrite: null, TestContext.Current!.Execution.CancellationToken));
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

		Assert.Equal([path], outcome.ChangedFiles);
		Assert.False(File.Exists(path), "rendering the diff writes nothing");
	}

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

		Assert.True(StartsWithMark(path), "the file arrived with a mark and has to keep it");
		Assert.Equal("class Marked { }", await File.ReadAllTextAsync(path, TestContext.Current!.Execution.CancellationToken));
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

		Assert.False(StartsWithMark(path), "nothing gave this file a mark to keep");
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

		Assert.False(StartsWithMark(path));
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
