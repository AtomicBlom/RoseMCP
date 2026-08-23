using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Reads source-generated documents.
/// <para>
/// This is the one thing no ordinary file tool can do. Generated sources live only inside the
/// compilation -- the compiler does not write them to disk unless a project opts in with
/// EmitCompilerGeneratedFiles -- so an agent looking at a diagnostic in generated code otherwise
/// has no way to see the code it is being told about.
/// </para>
/// </summary>
public static class GeneratedDocumentService
{
	public static async Task<GeneratedDocumentList> ListAsync(
		WorkspaceSnapshot snapshot,
		string? projectName,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var notices = new List<string>(snapshot.Notices);
		var projects = Select(snapshot.Solution, projectName, notices);
		var summaries = new List<GeneratedDocumentSummary>();
		var listed = 0;

		foreach (var project in projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// The first call to this on a project is what actually runs its generators, so on a
			// cold solution this loop is slow in a way the name of the tool does not suggest.
			progress?.Report(
				$"Running generators: {project.Name} ({listed + 1}/{projects.Count})",
				projects.Count == 0 ? 100 : 100.0 * listed / projects.Count);

			listed++;

			var generators = project.AnalyzerReferences
				.SelectMany(reference => SafeGetGenerators(reference, project.Language))
				.Count();

			var documents = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).ToArray();

			// An empty list means two very different things, and the caller cannot tell them apart.
			if (documents.Length == 0)
			{
				notices.Add(generators == 0
					? $"Project {project.Name} has no source generators."
					: $"Project {project.Name} loaded {generators} generator(s) but none produced output. "
						+ "Check rose_workspace_status for a missing analyzer assembly.");

				continue;
			}

			foreach (var document in documents)
			{
				var text = await document.GetTextAsync(cancellationToken);

				summaries.Add(new GeneratedDocumentSummary
				{
					Project = project.Name,
					HintName = document.HintName,
					FilePath = document.FilePath ?? string.Empty,
					LineCount = text.Lines.Count,
					CharacterCount = text.Length,
				});
			}
		}

		return new GeneratedDocumentList
		{
			Revision = snapshot.Revision,
			Documents = summaries,
			Notices = notices,
		};
	}

	public static async Task<GeneratedDocumentContent> ReadAsync(
		WorkspaceSnapshot snapshot,
		string hintName,
		string? projectName,
		CancellationToken cancellationToken)
	{
		var notices = new List<string>();
		var projects = Select(snapshot.Solution, projectName, notices);

		foreach (var project in projects)
		{
			foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
			{
				var matches = string.Equals(document.HintName, hintName, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(Path.GetFileName(document.FilePath), hintName, StringComparison.OrdinalIgnoreCase);

				if (!matches) continue;

				return new GeneratedDocumentContent
				{
					Revision = snapshot.Revision,
					Project = project.Name,
					HintName = document.HintName,
					Text = (await document.GetTextAsync(cancellationToken)).ToString(),
				};
			}
		}

		var available = await ListAsync(snapshot, projectName, cancellationToken);
		var names = available.Documents.Select(document => document.HintName).ToArray();

		throw new ArgumentException(names.Length == 0
			? $"No generated document named '{hintName}', and no generated documents exist. "
				+ "Check rose_workspace_status: a generator whose assembly is missing produces nothing silently."
			: $"No generated document named '{hintName}'. Available: {string.Join(", ", names)}");
	}

	private static IReadOnlyList<Project> Select(Solution solution, string? projectName, List<string> notices)
	{
		if (string.IsNullOrWhiteSpace(projectName)) return [.. solution.Projects];

		var matches = solution.Projects
			.Where(project => string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (matches.Length > 0) return matches;

		notices.Add($"No project named '{projectName}'; looked across the whole solution instead.");
		return [.. solution.Projects];
	}

	private static IEnumerable<ISourceGenerator> SafeGetGenerators(AnalyzerReference reference, string language)
	{
		try
		{
			return reference.GetGenerators(language);
		}
		catch (Exception)
		{
			return [];
		}
	}
}
