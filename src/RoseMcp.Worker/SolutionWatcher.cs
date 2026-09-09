using Microsoft.Extensions.Logging;

namespace RoseMcp.Worker;

/// <summary>
/// Watches the solution tree and tells the session when incremental absorption has stopped being
/// trustworthy.
/// <para>
/// The watcher is an optimisation, not the correctness mechanism -- the read barrier's stat sweep
/// is. What the watcher adds is the ability to notice the cases the sweep cannot see cheaply: files
/// appearing rather than changing, and bulk rewrites where FileSystemWatcher drops events out of
/// its buffer precisely when the most has changed.
/// </para>
/// </summary>
public sealed class SolutionWatcher : IDisposable
{
	/// <summary>
	/// Above this many events in one window, stop trusting the individual events. A branch switch
	/// or a bulk codegen run produces far more than this and overflows the watcher buffer anyway.
	/// </summary>
	private const int BulkChangeThreshold = 50;

	/// <summary>
	/// How many appearances are worth remembering individually. A trickle of new files never trips
	/// the bulk threshold, which is a window rather than a total, so the list needs a ceiling of its
	/// own -- and past a couple of hundred new files, reloading is cheaper than patching them in.
	/// </summary>
	private const int CreationsRemembered = 200;

	private static readonly TimeSpan BulkChangeWindow = TimeSpan.FromMilliseconds(500);

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
	private int _eventsInWindow;
	private DateTime _windowStartedUtc;

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
	/// eighteen. Dropping on the first leaks the rest, which then count toward the bulk-change
	/// threshold and force the very reload the suppression exists to avoid. Ignoring a genuine
	/// external write to the same file inside the window costs nothing, because the stat sweep is
	/// what makes a read correct and the watcher only decides how soon it hears.
	/// </para>
	/// </summary>
	private readonly Dictionary<string, DateTime> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Files seen appearing since the last barrier. Bounded: past <see cref="CreationsRemembered"/>
	/// the list stops being the cheaper answer and a reload is asked for instead.
	/// </summary>
	private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

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
			var created = _created.Count == 0 ? [] : _created.ToArray();

			_pending = WatchSignal.None;
			_created.Clear();
			PruneSelfWrites();

			if (!File.Exists(_solutionPath))
				signal |= WatchSignal.SolutionMissing;
			if (GitOperationInFlight())
				signal |= WatchSignal.GitOperationInFlight;

			return new WatchReport { Signal = signal, Created = created };
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

			var now = DateTime.UtcNow;
			if (now - _windowStartedUtc > BulkChangeWindow)
			{
				_windowStartedUtc = now;
				_eventsInWindow = 0;
			}

			_eventsInWindow++;
			_pending |= WatchSignal.FileChanges;

			// A rename is an appearance at the new path; the old one is dropped by the stat sweep,
			// which finds its file gone.
			if (e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed)
			{
				if (_created.Count >= CreationsRemembered)
				{
					_created.Clear();
					_pending |= WatchSignal.FullResyncRequired;
				}
				else
				{
					_created.Add(e.FullPath);
				}
			}

			// A checkout rewrites HEAD, which says the working tree is being replaced wholesale
			// rather than edited, and individual events stop being meaningful.
			var treeReplaced = _gitDirectory?.IsTreeReplaced(e.FullPath) ?? false;

			if (_eventsInWindow > BulkChangeThreshold || treeReplaced) _pending |= WatchSignal.FullResyncRequired;
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
	/// True while git holds its index lock, or a merge or rebase is part-way through. Reconciling
	/// then would read a tree that is half old and half new, so the barrier waits it out.
	/// </summary>
	private bool GitOperationInFlight() => _gitDirectory?.OperationInFlight() ?? false;

	/// <summary>
	/// Paths that never feed the snapshot. Build output churns constantly and would trip the
	/// bulk-change threshold on its own.
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
