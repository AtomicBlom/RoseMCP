using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Rename;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Renames a symbol across the solution using Roslyn's own renamer, so overrides, interface
/// implementations, partial declarations and cref references all move together.
/// </summary>
public static class RenameService
{
	public static async Task<MutationResult<RenameResult>> RenameAsync(
		WorkspaceSnapshot snapshot,
		RenameRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

		progress?.Report("Resolving the symbol", 0);

		var (symbol, _) = await SymbolLocator.ResolveAsync(
			snapshot.Solution, request.FilePath, request.Line, request.Column, cancellationToken);

		if (!symbol.Locations.Any(location => location.IsInSource))
		{
			throw new InvalidOperationException(
				$"'{symbol.Name}' comes from metadata, not source, so it cannot be renamed here.");
		}

		var options = new SymbolRenameOptions
		{
			RenameOverloads = request.RenameOverloads,
			RenameInComments = request.RenameInComments,
			RenameInStrings = request.RenameInStrings,

			// Renaming the file too would be a second, separate change to reason about. Keep the
			// rename to the symbol and let the caller move files deliberately.
			RenameFile = false,
		};

		// Roslyn's renamer offers no progress of its own, so the most that can be said is which
		// rename is under way. On a large solution this is where the time goes.
		progress?.Report($"Renaming {symbol.Name} to {request.NewName} across the solution", 10);

		var renamed = await Renamer.RenameSymbolAsync(
			snapshot.Solution, symbol, options, request.NewName, cancellationToken);

		var conflicts = await FindConflictsAsync(renamed, cancellationToken, progress.Slice(70, 95));

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 95);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, renamed, request.Apply, noteSelfWrite, cancellationToken);

		var notices = new List<string>(snapshot.Notices);
		if (!request.Apply) notices.Add("Preview only; nothing was written to disk.");
		if (outcome.ChangedFiles.Count == 0) notices.Add("The rename produced no changes.");

		var result = new RenameResult
		{
			Revision = snapshot.Revision,
			OldName = symbol.Name,
			NewName = request.NewName,
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			FilesChanged = outcome.ChangedFiles.Count,
			Diff = outcome.Diff,
			Conflicts = conflicts,
			Notices = notices,
		};

		// Only publish the new solution when it was actually written; a preview must not advance
		// the revision or the snapshot would disagree with disk.
		return new MutationResult<RenameResult>(result, result.Applied ? renamed : null);
	}

	/// <summary>
	/// Places where the new name would bind to something else or shadow an existing member. Roslyn
	/// marks these with a conflict annotation rather than refusing, so they have to be surfaced
	/// deliberately -- applying them silently is how a rename quietly changes behaviour.
	/// </summary>
	private static async Task<IReadOnlyList<string>> FindConflictsAsync(
		Solution solution,
		CancellationToken cancellationToken,
		IWorkProgress? progress)
	{
		var conflicts = new List<string>();
		var inspected = 0;
		var total = solution.ProjectIds.Count;

		foreach (var project in solution.Projects)
		{
			progress?.Report(
				$"Checking {project.Name} for conflicts ({inspected + 1}/{total})",
				total == 0 ? 100 : 100.0 * inspected / total);

			inspected++;

			foreach (var document in project.Documents)
			{
				var root = await document.GetSyntaxRootAsync(cancellationToken);
				if (root is null) continue;

				foreach (var node in root.GetAnnotatedNodesAndTokens(ConflictAnnotation.Kind))
				{
					var line = node.GetLocation()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
					var description = ConflictAnnotation.GetDescription(
						node.GetAnnotations(ConflictAnnotation.Kind).First());

					conflicts.Add($"{document.FilePath}:{line}: {description}");
				}
			}
		}

		return conflicts;
	}
}
