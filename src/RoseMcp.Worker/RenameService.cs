using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Rename;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Options a caller can vary on a rename.</summary>
public sealed record RenameRequest
{
	public required string FilePath { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	public required string NewName { get; init; }

	public bool RenameOverloads { get; init; }

	public bool RenameInComments { get; init; }

	public bool RenameInStrings { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Fail rather than apply if the workspace has moved past this revision. Matters when more than
	/// one client shares a broker in http mode.
	/// </summary>
	public long? ExpectedRevision { get; init; }
}

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
		CancellationToken cancellationToken)
	{
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

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

		var renamed = await Renamer.RenameSymbolAsync(
			snapshot.Solution, symbol, options, request.NewName, cancellationToken);

		var conflicts = await FindConflictsAsync(renamed, cancellationToken);

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
	private static async Task<IReadOnlyList<string>> FindConflictsAsync(Solution solution, CancellationToken cancellationToken)
	{
		var conflicts = new List<string>();

		foreach (var project in solution.Projects)
		{
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
