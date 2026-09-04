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
	[Fact]
	public async Task Refuses_to_write_an_added_document_whose_path_is_relative()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: "Added.cs");

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current.CancellationToken));

		Assert.Contains("Added.cs", failure.Message, StringComparison.Ordinal);
		Assert.Contains("relative path", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Refuses_to_write_a_changed_document_whose_path_is_relative()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var documentId = DocumentId.CreateNewId(project.Id);
		var before = project.Solution.AddDocument(documentId, "Existing.cs", "class Existing;", filePath: "Existing.cs");

		var after = before.WithDocumentText(documentId, SourceText.From("class Existing { }"));

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: true, noteSelfWrite: null, TestContext.Current.CancellationToken));

		Assert.Contains("Existing.cs", failure.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// The refusal is not a write-time check: a preview asks the same question, because a diff
	/// naming a path nobody can write is as misleading as the write itself.
	/// </summary>
	[Fact]
	public async Task Refuses_a_relative_path_even_when_not_writing()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: "Added.cs");

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => SolutionWriter.ApplyAsync(before, after, write: false, noteSelfWrite: null, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Renders_a_rooted_added_document_without_writing_it()
	{
		using var workspace = new AdhocWorkspace();
		var project = Empty(workspace);
		var before = project.Solution;
		var path = Path.Combine(Path.GetTempPath(), "RoseMcpSolutionWriterTests", "Added.cs");

		var after = before.AddDocument(DocumentId.CreateNewId(project.Id), "Added.cs", "class Added;", filePath: path);

		var outcome = await SolutionWriter.ApplyAsync(before, after, write: false, noteSelfWrite: null, TestContext.Current.CancellationToken);

		Assert.Equal([path], outcome.ChangedFiles);
		Assert.False(File.Exists(path));
	}

	private static Project Empty(AdhocWorkspace workspace) =>
		workspace.AddProject("Sample", LanguageNames.CSharp);
}
