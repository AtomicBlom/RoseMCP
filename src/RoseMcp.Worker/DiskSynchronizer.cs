using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>How a tracked file participates in the solution, which decides how it is updated.</summary>
public enum TrackedDocumentKind
{
	Source,
	Additional,
	AnalyzerConfig,
}

/// <summary>What the last sweep saw for a file, cheap enough to compare on every read.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
	public static FileStamp? For(string path)
	{
		var info = new FileInfo(path);
		return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
	}
}

/// <summary>Outcome of one reconciliation sweep.</summary>
public sealed record DiskSyncResult
{
	public required Solution Solution { get; init; }

	public required int ChangedCount { get; init; }

	public required int RemovedCount { get; init; }

	/// <summary>
	/// A project file, props file, or the solution itself changed. Text patching cannot represent
	/// that, so the caller has to reload rather than carry on with a patched snapshot.
	/// </summary>
	public required bool StructuralChange { get; init; }

	/// <summary>Files that could not be read this sweep, usually because a write was in progress.</summary>
	public required IReadOnlyList<string> Deferred { get; init; }

	public bool AnythingChanged => ChangedCount > 0 || RemovedCount > 0 || StructuralChange;
}

/// <summary>
/// Keeps a solution snapshot honest against what is actually on disk.
/// <para>
/// This is the answer to stale diagnostics. The agent edits files with its own tools and never
/// tells the workspace, so before any read is served every tracked document is stat-checked and
/// re-read if it moved. A file watcher is a latency optimisation on top of this; correctness does
/// not depend on one, which matters because watchers drop events under exactly the conditions that
/// change the most files.
/// </para>
/// </summary>
public sealed class DiskSynchronizer
{
	private readonly Dictionary<DocumentId, TrackedDocument> _documents = [];
	private readonly Dictionary<string, FileStamp?> _structuralFiles = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Rebuilds the tracking table from a freshly loaded solution.</summary>
	public void Reset(Solution solution, string solutionPath)
	{
		_documents.Clear();
		_structuralFiles.Clear();

		foreach (var project in solution.Projects)
		{
			Track(project.Documents, TrackedDocumentKind.Source);
			Track(project.AdditionalDocuments, TrackedDocumentKind.Additional);
			Track(project.AnalyzerConfigDocuments, TrackedDocumentKind.AnalyzerConfig);

			if (project.FilePath is { Length: > 0 } projectFile)
				TrackStructural(projectFile);
		}

		TrackStructural(solutionPath);
		foreach (var influence in BuildInfluencingFiles(solutionPath))
			TrackStructural(influence);
	}

	/// <summary>
	/// Starts tracking documents that have appeared in the snapshot since the last sweep -- which
	/// means the ones this worker added itself, since anything else arrives through a reload.
	/// <para>
	/// Without this a file the worker created is in the snapshot but not in the tracking table, so
	/// the next edit anyone makes to it would be invisible until something forced a reload. That is
	/// precisely the staleness this class exists to prevent.
	/// </para>
	/// </summary>
	public void TrackNew(Solution solution)
	{
		var known = _documents.Values.Select(document => document.Id).ToHashSet();

		foreach (var project in solution.Projects)
		{
			foreach (var document in project.Documents)
			{
				if (known.Contains(document.Id)) continue;
				if (document.FilePath is not { Length: > 0 } path) continue;

				_documents[document.Id] = new TrackedDocument(
					document.Id, path, TrackedDocumentKind.Source, FileStamp.For(path));
			}
		}
	}

	/// <summary>
	/// Restamps a file after this worker wrote it, so the next sweep sees it as already current.
	/// </summary>
	public void AcceptSelfWrite(DocumentId id, string path)
	{
		if (_documents.TryGetValue(id, out var tracked))
			_documents[id] = tracked with { Stamp = FileStamp.For(path) };
	}

	/// <summary>
	/// Reconciles the snapshot with disk. Runs on the session's single writer, so it never races a
	/// mutation, and returns the updated snapshot rather than mutating anything in place.
	/// </summary>
	public async Task<DiskSyncResult> SyncAsync(Solution solution, CancellationToken cancellationToken)
	{
		var changed = 0;
		var removed = 0;
		List<string>? deferred = null;
		List<DocumentId>? dropped = null;

		foreach (var (id, tracked) in _documents.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();

			var stamp = FileStamp.For(tracked.Path);

			if (stamp is null)
			{
				solution = Remove(solution, tracked);
				(dropped ??= []).Add(id);
				removed++;
				continue;
			}

			if (stamp == tracked.Stamp) continue;

			var text = await TryReadAsync(tracked.Path, cancellationToken);
			if (text is null)
			{
				// Caught mid-write. Leave the stamp alone so the next sweep retries rather than
				// ingesting a truncated file as the truth.
				(deferred ??= []).Add(tracked.Path);
				continue;
			}

			solution = WithText(solution, tracked, text);
			_documents[id] = tracked with { Stamp = stamp };
			changed++;
		}

		if (dropped is not null)
		{
			foreach (var id in dropped)
			{
				_documents.Remove(id);
			}
		}

		return new DiskSyncResult
		{
			Solution = solution,
			ChangedCount = changed,
			RemovedCount = removed,
			StructuralChange = DetectStructuralChange(),
			Deferred = (IReadOnlyList<string>?)deferred ?? [],
		};
	}

	private bool DetectStructuralChange()
	{
		var changed = false;

		foreach (var (path, previous) in _structuralFiles.ToArray())
		{
			var current = FileStamp.For(path);
			if (current == previous) continue;

			_structuralFiles[path] = current;
			changed = true;
		}

		return changed;
	}

	private static Solution Remove(Solution solution, TrackedDocument tracked) => tracked.Kind switch
	{
		TrackedDocumentKind.Source => solution.RemoveDocument(tracked.Id),
		TrackedDocumentKind.Additional => solution.RemoveAdditionalDocument(tracked.Id),
		_ => solution.RemoveAnalyzerConfigDocument(tracked.Id),
	};

	private static Solution WithText(Solution solution, TrackedDocument tracked, SourceText text) => tracked.Kind switch
	{
		TrackedDocumentKind.Source => solution.WithDocumentText(tracked.Id, text, PreservationMode.PreserveValue),
		TrackedDocumentKind.Additional => solution.WithAdditionalDocumentText(tracked.Id, text, PreservationMode.PreserveValue),
		_ => solution.WithAnalyzerConfigDocumentText(tracked.Id, text, PreservationMode.PreserveValue),
	};

	private static async Task<SourceText?> TryReadAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = new FileStream(
				path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);

			using var buffer = new MemoryStream();
			await stream.CopyToAsync(buffer, cancellationToken);
			buffer.Position = 0;

			return SourceText.From(buffer, Encoding.UTF8, canBeEmbedded: false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private void Track(IEnumerable<TextDocument> documents, TrackedDocumentKind kind)
	{
		foreach (var document in documents)
		{
			if (document.FilePath is not { Length: > 0 } path) continue;

			_documents[document.Id] = new TrackedDocument(document.Id, path, kind, FileStamp.For(path));
		}
	}

	private void TrackStructural(string path) => _structuralFiles[Path.GetFullPath(path)] = FileStamp.For(path);

	/// <summary>
	/// Files that change how projects evaluate without appearing in any project. Editing
	/// Directory.Packages.props rewrites the reference graph while every csproj stays untouched.
	/// </summary>
	private static IEnumerable<string> BuildInfluencingFiles(string solutionPath)
	{
		string[] names =
		[
			"Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
			"global.json", "nuget.config", "NuGet.config", "NuGet.Config",
		];

		var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
		while (!string.IsNullOrEmpty(directory))
		{
			foreach (var name in names)
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
					yield return candidate;
			}

			directory = Path.GetDirectoryName(directory);
		}
	}
}

/// <summary>One file the synchronizer watches, and what it looked like last time.</summary>
internal readonly record struct TrackedDocument(DocumentId Id, string Path, TrackedDocumentKind Kind, FileStamp? Stamp);
