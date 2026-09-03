using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

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
	private static readonly char[] SeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	/// <summary>
	/// Files whose appearance changes how projects evaluate, and which are not identified by their
	/// extension. Deliberately named one by one: any new .json would otherwise force a reload, and
	/// an agent writing code creates those for reasons that have nothing to do with the build.
	/// </summary>
	private static readonly HashSet<string> StructuralNames = new(StringComparer.OrdinalIgnoreCase)
	{
		".editorconfig", "global.json", "nuget.config", "packages.config", "rosemcp.json",
	};

	/// <summary>
	/// Directories that hold no source the project compiles. The same list the watcher ignores, for
	/// the same reasons.
	/// </summary>
	private static readonly HashSet<string> IgnoredDirectories =
		new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", "node_modules" };

	private readonly Dictionary<DocumentId, TrackedDocument> _documents = [];
	private readonly Dictionary<string, FileStamp?> _structuralFiles = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Whether each project globs its source files, which costs one read of the project file and
	/// never changes without the project file changing -- and that is a reload.
	/// </summary>
	private readonly Dictionary<string, bool> _globs = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Files found on disk and deliberately not added, so the reason is given once rather than on
	/// every read for as long as the file stays out of its project.
	/// </summary>
	private readonly HashSet<string> _declined = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Rebuilds the tracking table from a freshly loaded solution.</summary>
	public void Reset(Solution solution, string solutionPath)
	{
		_documents.Clear();
		_structuralFiles.Clear();
		_globs.Clear();
		_declined.Clear();

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
	/// Adds files that have appeared on disk to the project whose directory holds them.
	/// <para>
	/// Without this a new source file is invisible. The sweep stats the documents it knows about and
	/// a new file is not one of them, so the workspace goes on compiling a solution that does not
	/// include it -- and every reference to the new type reports CS0103, which is indistinguishable
	/// from the code actually being wrong. That is the worst failure this server can have: not an
	/// error, but a confident answer about a file that is not there. An agent that writes C# creates
	/// files constantly, so it was also the failure most likely to happen.
	/// </para>
	/// <para>
	/// A reload would also fix it and is what this used to need, at the cost of a full design-time
	/// build per new file. Adding the document is the same operation a mutation that creates a file
	/// already performs, and the attribution is the honest part: containment in a project's
	/// directory, which is exactly what the SDK's default globs compile, and a refusal to claim
	/// anything for a project that lists its files instead.
	/// </para>
	/// </summary>
	public async Task<NewFileResult> AbsorbNewAsync(
		Solution solution,
		IReadOnlyList<string> created,
		CancellationToken cancellationToken)
	{
		// The watcher's list is used for one thing only: a project or build file appearing, which
		// nothing here can patch in and which a directory walk for source files would not see.
		var structural = created.Any(IsStructural);

		var added = new List<string>();
		var notInTheBuild = new List<string>();

		foreach (var path in Untracked(solution, cancellationToken))
		{
			cancellationToken.ThrowIfCancellationRequested();

			var owners = Owners(solution, path);
			if (owners.Count == 0) continue;

			if (!Globs(owners[0]))
			{
				// Once. The file stays untracked for as long as it stays out of the project, and
				// repeating the notice on every read afterwards would bury everything else.
				if (_declined.Add(path)) notInTheBuild.Add(path);
				continue;
			}

			var text = await TryReadAsync(path, cancellationToken);
			if (text is null) continue;

			var stamp = FileStamp.For(path);

			// Every project whose directory holds it, not just one. A multi-targeted project is
			// several projects over one file, and a file missing from all but the first would report
			// errors for the frameworks it was left out of.
			foreach (var project in owners)
			{
				var id = DocumentId.CreateNewId(project.Id, Path.GetFileName(path));

				solution = solution.AddDocument(id, Path.GetFileName(path), text, Folders(project, path), path);
				_documents[id] = new TrackedDocument(id, path, TrackedDocumentKind.Source, stamp);
			}

			added.Add(path);
		}

		return new NewFileResult
		{
			Solution = solution,
			Added = added,
			StructuralChange = structural,
			NotInTheBuild = notInTheBuild,
		};
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

	/// <summary>
	/// Source files on disk that no document covers.
	/// <para>
	/// Found by walking the project directories rather than by trusting the watcher, for exactly the
	/// reason the rest of this class stats rather than trusts it: a dropped or late event has to cost
	/// latency and not correctness. A watcher event arrives some milliseconds after the write, so an
	/// agent that creates a file and immediately asks about it would get an answer that depended on
	/// the race -- which is worse than a slow answer and much worse than a wrong one, because it is
	/// both intermittent and confident.
	/// </para>
	/// <para>
	/// The cost is one pruned enumeration of the source tree per barrier, against the one stat per
	/// tracked document the sweep already pays. Enumerating a directory returns its entries in bulk,
	/// so this is the cheaper half of the two.
	/// </para>
	/// </summary>
	private IReadOnlyList<string> Untracked(Solution solution, CancellationToken cancellationToken)
	{
		var tracked = _documents.Values
			.Select(document => Path.GetFullPath(document.Path))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var found = new List<string>();

		foreach (var root in Roots(solution))
		{
			Walk(root, tracked, found, cancellationToken);
		}

		return found;
	}

	private static void Walk(
		string directory,
		HashSet<string> tracked,
		List<string> found,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
			{
				var path = Path.GetFullPath(file);
				if (!tracked.Contains(path)) found.Add(path);
			}

			foreach (var child in Directory.EnumerateDirectories(directory))
			{
				var name = Path.GetFileName(child);

				// Build output holds thousands of files and generated sources that belong to the
				// compiler rather than to the project, and a dot directory belongs to a tool.
				if (IgnoredDirectories.Contains(name) || name.StartsWith('.')) continue;

				Walk(child, tracked, found, cancellationToken);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A directory that cannot be read this time is one the next barrier tries again.
		}
	}

	/// <summary>
	/// The directories to walk: every project's own, with any that sits inside another dropped, so a
	/// project nested in another project's folder is not walked twice.
	/// </summary>
	private static IReadOnlyList<string> Roots(Solution solution)
	{
		var directories = solution.Projects
			.Select(project => project.FilePath)
			.OfType<string>()
			.Select(file => Path.GetDirectoryName(Path.GetFullPath(file)))
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(directory => directory.Length)
			.ToArray();

		var roots = new List<string>();

		foreach (var directory in directories)
		{
			if (roots.Any(root => Encloses(root, directory))) continue;

			roots.Add(directory);
		}

		return roots;
	}

	/// <summary>
	/// The projects that would compile a file in that place: the ones whose directory is the closest
	/// enclosing one. Closest, because a repository nests projects inside other projects' folders
	/// often enough that the outermost would claim half the tree.
	/// </summary>
	private static IReadOnlyList<Project> Owners(Solution solution, string path)
	{
		var owners = new List<Project>();
		var deepest = -1;

		foreach (var project in solution.Projects)
		{
			if (project.FilePath is not { Length: > 0 } file) continue;
			if (Path.GetDirectoryName(Path.GetFullPath(file)) is not { Length: > 0 } directory) continue;
			if (!Encloses(directory, path)) continue;

			if (directory.Length > deepest)
			{
				deepest = directory.Length;
				owners.Clear();
			}

			// Two projects sharing a directory both glob it, so both really do compile the file.
			if (directory.Length == deepest) owners.Add(project);
		}

		return owners;
	}

	/// <summary>
	/// Folders as Roslyn means them: the path from the project directory down to the file. Worth
	/// setting rather than leaving empty, because it is what tells an analyzer whether a namespace
	/// matches the folder the file lives in.
	/// </summary>
	private static IReadOnlyList<string> Folders(Project project, string path)
	{
		if (project.FilePath is not { Length: > 0 } file) return [];
		if (Path.GetDirectoryName(Path.GetFullPath(file)) is not { Length: > 0 } root) return [];
		if (Path.GetDirectoryName(path) is not { Length: > 0 } directory) return [];

		var relative = Path.GetRelativePath(root, directory);
		if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal)) return [];

		return relative.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);
	}

	private bool Globs(Project project)
	{
		if (project.FilePath is not { Length: > 0 } file) return true;

		var path = Path.GetFullPath(file);
		if (_globs.TryGetValue(path, out var known)) return known;

		bool globs;
		try
		{
			globs = ProjectItemStyle.GlobsSourceFiles(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			globs = true;
		}

		_globs[path] = globs;

		return globs;
	}

	/// <summary>
	/// Files that change how a project evaluates rather than what is in it. None of them can be
	/// patched into a snapshot, and an .editorconfig is among them because it decides what the
	/// analyzers and the formatter do to every file beneath it.
	/// </summary>
	private static bool IsStructural(string path)
	{
		if (StructuralNames.Contains(Path.GetFileName(path))) return true;

		return Path.GetExtension(path).ToLowerInvariant() is
			".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".slnf";
	}

	private static bool IsSource(string path) =>
		string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

	private static bool Encloses(string directory, string path) =>
		path.StartsWith(directory.TrimEnd(SeparatorChars) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
