using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Compiles what an edit produced, and reports what it changed about the errors rather than what
/// errors there are.
/// <para>
/// The difference is the whole point. A project mid-refactor has errors already, and a list of them
/// answers a question nobody asked: an agent needs to know whether the edit it just made was sound,
/// and a hundred pre-existing errors bury the two that are its fault. Comparing before with after
/// costs a second compilation and turns the answer from a haystack into a sentence.
/// </para>
/// <para>
/// Only the projects that hold the edited file are compiled. Everything that references them could
/// also break -- which is exactly what a signature change does -- but compiling a whole solution
/// twice on every member edit would cost more than the build this exists to avoid, so the projects
/// checked are named in the answer and rose_diagnostics covers the rest.
/// </para>
/// </summary>
public static class EditVerification
{
	public static async Task<Verification> RunAsync(
		DiagnosticsService diagnostics,
		Solution before,
		Solution after,
		string filePath,
		CancellationToken cancellationToken)
	{
		var projects = after.GetDocumentIdsWithFilePath(filePath)
			.Select(id => after.GetProject(id.ProjectId))
			.OfType<Project>()
			.Select(project => project.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		if (projects.Length == 0) return Verification.NotRun;

		var was = await ErrorsAsync(diagnostics, before, projects, cancellationToken);
		var now = await ErrorsAsync(diagnostics, after, projects, cancellationToken);

		var (introduced, resolved) = Delta(was, now);

		return new Verification
		{
			Ran = true,
			Introduced = Ordered(introduced, filePath),
			ResolvedCount = resolved,
			TotalCount = now.Count,
			Projects = projects,
		};
	}

	/// <summary>
	/// Errors only, and every one of them.
	/// <para>
	/// Errors only because a warning is not a broken edit -- except where the repository says it is,
	/// and there the compilation reports its warnings as errors already, so this needs no setting of
	/// its own to follow. Every one of them because the delta is computed from these two lists, and
	/// a list truncated at the usual two hundred would make the comparison say whatever the cut-off
	/// happened to drop.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<DiagnosticEntry>> ErrorsAsync(
		DiagnosticsService diagnostics,
		Solution solution,
		IReadOnlyList<string> projects,
		CancellationToken cancellationToken)
	{
		var snapshot = new WorkspaceSnapshot { Solution = solution, Revision = 0 };
		var collected = new List<DiagnosticEntry>();

		foreach (var project in projects)
		{
			var result = await diagnostics.AnalyseAsync(
				snapshot,
				new DiagnosticsRequest
				{
					Scope = DiagnosticScope.Project,
					Target = project,
					MinimumSeverity = DiagnosticSeverity.Error,
					MaxResults = int.MaxValue,
				},
				cancellationToken);

			collected.AddRange(result.Diagnostics);
		}

		return collected;
	}

	/// <summary>
	/// Which errors are new, matched on everything except position.
	/// <para>
	/// Position is left out deliberately: an edit that adds three lines moves every diagnostic below
	/// it, and a key including the line would report each one as both resolved and introduced --
	/// turning a clean edit into a page of noise.
	/// </para>
	/// </summary>
	private static (IReadOnlyList<DiagnosticEntry> Introduced, int Resolved) Delta(
		IReadOnlyList<DiagnosticEntry> before,
		IReadOnlyList<DiagnosticEntry> after)
	{
		var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (var entry in before)
		{
			remaining[Key(entry)] = remaining.GetValueOrDefault(Key(entry)) + 1;
		}

		var introduced = new List<DiagnosticEntry>();

		foreach (var entry in after)
		{
			var key = Key(entry);

			// Counted rather than matched, so two identical errors in one file are two errors: fixing
			// one of them is a real change and has to show as one.
			if (remaining.TryGetValue(key, out var count) && count > 0)
			{
				remaining[key] = count - 1;
				continue;
			}

			introduced.Add(entry);
		}

		return (introduced, remaining.Values.Sum());
	}

	/// <summary>
	/// The edited file first. An error there is usually the cause and an error elsewhere usually the
	/// consequence, and a caller reading only the first line of the answer should get the cause.
	/// </summary>
	private static IReadOnlyList<DiagnosticEntry> Ordered(IReadOnlyList<DiagnosticEntry> introduced, string filePath) =>
		[
			.. introduced
				.OrderByDescending(entry => SamePath(entry.FilePath, filePath))
				.ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.Line),
		];

	private static string Key(DiagnosticEntry entry) => $"{entry.Id}|{entry.FilePath}|{entry.Message}";

	private static bool SamePath(string? left, string right) =>
		left is { Length: > 0 } && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
