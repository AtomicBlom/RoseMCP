using Microsoft.Extensions.Logging;
using RoseMcp.Solutions;

namespace RoseMcp.Worker;

/// <summary>
/// Watches the solution tree for what a read barrier cannot find for itself.
/// <para>
/// The watcher is an optimisation, not the correctness mechanism -- the read barrier's stat sweep and
/// directory walk are. Between them they find every tracked document that changed and every source
/// file that appeared, however many, so the watcher neither counts events nor remembers source files.
/// What it adds is what a read cannot see cheaply: a build file appearing, a build file changing that
/// the sweep does not track, HEAD being rewritten, and the event stream itself failing.
/// </para>
/// </summary>
public sealed class SolutionWatcher : IDisposable
{
	/// <summary>
	/// How long a write of ours goes on suppressing events for its file. Long enough to cover the
	/// several events one rewrite raises, and short enough that a later external edit to the same
	/// file is still heard promptly -- which the stat sweep would catch either way.
	/// </summary>
	private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromSeconds(5);

	private static readonly char[] SeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	private static readonly HashSet<string> IgnoredDirectories =
		new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", "node_modules" };

	private readonly Lock _gate = new();
	private readonly string _solutionPath;
	private readonly string _root;
	private readonly GitDirectory? _gitDirectory;
	private readonly ILogger<SolutionWatcher> _logger;

	private FileSystemWatcher? _watcher;
	private WatchSignal _pending;

	public SolutionWatcher(string solutionPath, ILogger<SolutionWatcher> logger)
	{
		_solutionPath = Path.GetFullPath(solutionPath);
		_root = Path.GetDirectoryName(_solutionPath) ?? ".";
		_gitDirectory = GitDirectory.Find(_root);
		_logger = logger;

		Start();
	}

	/// <summary>
	/// Files this worker wrote itself and when, so its own edits do not bounce back as external ones.
	/// <para>
	/// Held for a window rather than dropped on the first matching event, because one rewrite raises
	/// more than one event: with this watcher's NotifyFilter, ten rewrites of existing files raise
	/// eighteen. Dropping on the first leaks the rest back as somebody else's edits. Ignoring a genuine
	/// external write to the same file inside the window costs nothing, because the stat sweep is what
	/// makes a read correct and the watcher only decides how soon it hears.
	/// </para>
	/// </summary>
	private readonly Dictionary<string, DateTime> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Build files seen appearing since the last barrier, one renamed into place included.</summary>
	private readonly HashSet<string> _appeared = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Build files seen changing or going away since the last barrier, one renamed away included.</summary>
	private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Records that the write about to land on this path is ours, so it is absorbed silently rather
	/// than bouncing back on the next barrier as an external edit.
	/// </summary>
	public void NoteSelfWrite(string path)
	{
		lock (_gate)
		{
			_selfWrites[Path.GetFullPath(path)] = DateTime.UtcNow;
		}
	}

	/// <summary>
	/// Whether a git operation is in flight, without consuming anything. Draining to answer this
	/// would discard the file-change signals that accumulate during the operation.
	/// </summary>
	public bool IsGitOperationInFlight() => GitOperationInFlight();

	/// <summary>Takes and clears what has accumulated since the last call.</summary>
	public WatchReport Drain()
	{
		lock (_gate)
		{
			var signal = _pending;
			IReadOnlyList<string> appeared = _appeared.Count == 0 ? [] : [.. _appeared];
			IReadOnlyList<string> changed = _changed.Count == 0 ? [] : [.. _changed];

			_pending = WatchSignal.None;
			_appeared.Clear();
			_changed.Clear();
			PruneSelfWrites();

			if (!File.Exists(_solutionPath)) signal |= WatchSignal.SolutionMissing;
			if (GitOperationInFlight()) signal |= WatchSignal.GitOperationInFlight;

			return new WatchReport { Signal = signal, BuildFilesAppeared = appeared, BuildFilesChanged = changed };
		}
	}

	private void Start()
	{
		try
		{
			var watcher = new FileSystemWatcher(_root)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
					| NotifyFilters.LastWrite | NotifyFilters.Size,

				// The default 8KB buffer overflows almost immediately on a branch switch. A larger
				// one narrows the window, but overflow is still handled rather than prevented.
				InternalBufferSize = 64 * 1024,
			};

			watcher.Changed += OnChanged;
			watcher.Created += OnChanged;
			watcher.Deleted += OnChanged;
			watcher.Renamed += OnChanged;
			watcher.Error += OnError;
			watcher.EnableRaisingEvents = true;

			_watcher = watcher;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			// Without a watcher every read still reconciles; it just costs a full sweep each time.
			_logger.LogWarning(exception, "Could not watch {Root}; falling back to stat sweeps alone.", _root);
			Signal(WatchSignal.FullResyncRequired);
		}
	}

	private void OnChanged(object sender, FileSystemEventArgs e)
	{
		if (Ignorable(e.FullPath)) return;

		lock (_gate)
		{
			if (IsSelfWrite(e.FullPath)) return;

			_pending |= WatchSignal.FileChanges;
			Record(e);

			// A checkout rewrites HEAD, which says the working tree is being replaced wholesale
			// rather than edited, and individual events stop being meaningful.
			var treeReplaced = _gitDirectory?.IsTreeReplaced(e.FullPath) ?? false;
			if (treeReplaced) _pending |= WatchSignal.FullResyncRequired;
		}
	}

	/// <summary>
	/// Remembers what happened to a build file, and nothing for any other kind of file. Called under the
	/// gate. A rename is an appearance at its new path and a removal at its old one, since either name
	/// can be a build file on its own.
	/// </summary>
	private void Record(FileSystemEventArgs e)
	{
		if (e is RenamedEventArgs renamed && BuildInfluencingFiles.IsBuildFile(renamed.OldFullPath))
		{
			_changed.Add(renamed.OldFullPath);
		}

		if (!BuildInfluencingFiles.IsBuildFile(e.FullPath)) return;

		var appeared = e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed;
		if (appeared)
		{
			_appeared.Add(e.FullPath);
		}
		else
		{
			_changed.Add(e.FullPath);
		}
	}

	/// <summary>
	/// Whether this event is one of our own writes, still inside its window. Called under the gate.
	/// An expired entry is dropped as it is found, so a file written repeatedly never accumulates.
	/// </summary>
	private bool IsSelfWrite(string path)
	{
		if (!_selfWrites.TryGetValue(path, out var written)) return false;
		if (DateTime.UtcNow - written <= SelfWriteWindow) return true;

		_selfWrites.Remove(path);

		return false;
	}

	/// <summary>
	/// Drops self-write entries whose window has passed. A file written once and never touched again
	/// raises no further event to expire its entry, so without this the table grows for the life of
	/// the process -- which is hours, editing C#.
	/// </summary>
	private void PruneSelfWrites()
	{
		if (_selfWrites.Count == 0) return;

		var cutoff = DateTime.UtcNow - SelfWriteWindow;

		foreach (var (path, written) in _selfWrites.ToArray())
		{
			if (written < cutoff) _selfWrites.Remove(path);
		}
	}

	/// <summary>
	/// Raised when the buffer overflows or the watched directory disappears. Either way the event
	/// stream has holes in it, so nothing incremental can be trusted until a full reconcile.
	/// </summary>
	private void OnError(object sender, ErrorEventArgs e)
	{
		_logger.LogWarning(e.GetException(), "The file watcher failed; forcing a full resync.");
		Signal(WatchSignal.FullResyncRequired);

		// A deleted root kills the watcher permanently, so rebuild it if the root is still there.
		if (!Directory.Exists(_root)) return;

		_watcher?.Dispose();
		_watcher = null;
		Start();
	}

	private void Signal(WatchSignal signal)
	{
		lock (_gate)
		{
			_pending |= signal;
		}
	}

	/// <summary>
	/// True while git holds its index lock. Reconciling then would read a tree that is half old and half
	/// new, so the barrier waits it out.
	/// </summary>
	private bool GitOperationInFlight() => _gitDirectory?.OperationInFlight() ?? false;

	/// <summary>
	/// Paths that never feed the snapshot. Build output churns constantly and says nothing about the
	/// solution, so its events are not worth taking the gate for.
	/// </summary>
	private bool Ignorable(string path)
	{
		// Restore rewriting the assets file means the reference graph moved, so that one counts.
		if (path.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase)) return false;

		if (_gitDirectory is { } git && git.Contains(path)) return !git.IsTreeReplaced(path);

		foreach (var segment in path.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries))
		{
			if (IgnoredDirectories.Contains(segment)) return true;
		}

		return false;
	}

	public void Dispose() => _watcher?.Dispose();
}
