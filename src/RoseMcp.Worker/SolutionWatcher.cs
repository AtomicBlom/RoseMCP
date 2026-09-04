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

	private static readonly char[] SeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	private static readonly HashSet<string> IgnoredDirectories =
		new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", "node_modules" };

	private readonly Lock _gate = new();
	private readonly string _solutionPath;
	private readonly string _root;
	private readonly string? _gitDirectory;
	private readonly ILogger<SolutionWatcher> _logger;

	private FileSystemWatcher? _watcher;
	private WatchSignal _pending;
	private int _eventsInWindow;
	private DateTime _windowStartedUtc;

	public SolutionWatcher(string solutionPath, ILogger<SolutionWatcher> logger)
	{
		_solutionPath = Path.GetFullPath(solutionPath);
		_root = Path.GetDirectoryName(_solutionPath) ?? ".";
		_gitDirectory = FindGitDirectory(_root);
		_logger = logger;

		Start();
	}

	/// <summary>Files this worker wrote itself, so its own edits do not bounce back as external ones.</summary>
	private readonly HashSet<string> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Files seen appearing since the last barrier. Bounded: past <see cref="CreationsRemembered"/>
	/// the list stops being the cheaper answer and a reload is asked for instead.
	/// </summary>
	private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

	public void NoteSelfWrite(string path)
	{
		lock (_gate)
		{
			_selfWrites.Add(Path.GetFullPath(path));
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
			if (_selfWrites.Remove(e.FullPath)) return;

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

			// A checkout rewrites HEAD or index, which says the working tree is being replaced
			// wholesale rather than edited. Either way individual events stop being meaningful.
			var treeReplaced = IsGitInternal(e.FullPath) && IsGitTreeMarker(e.FullPath);

			if (_eventsInWindow > BulkChangeThreshold || treeReplaced) _pending |= WatchSignal.FullResyncRequired;
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
	private bool GitOperationInFlight()
	{
		if (_gitDirectory is null) return false;

		return File.Exists(Path.Combine(_gitDirectory, "index.lock"))
			|| File.Exists(Path.Combine(_gitDirectory, "MERGE_HEAD"))
			|| File.Exists(Path.Combine(_gitDirectory, "REBASE_HEAD"));
	}

	/// <summary>
	/// Paths that never feed the snapshot. Build output churns constantly and would trip the
	/// bulk-change threshold on its own.
	/// </summary>
	private bool Ignorable(string path)
	{
		// Restore rewriting the assets file means the reference graph moved, so that one counts.
		if (path.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase)) return false;

		if (IsGitInternal(path)) return !IsGitTreeMarker(path);

		foreach (var segment in path.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries))
		{
			if (IgnoredDirectories.Contains(segment)) return true;
		}

		return false;
	}

	private bool IsGitInternal(string path) =>
		_gitDirectory is not null && path.StartsWith(_gitDirectory, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// HEAD and index are the two git files that imply the working tree is being replaced. The
	/// rest of .git -- objects, logs, packed refs -- churns during a fetch that touches no source
	/// at all, so treating any .git write as a resync trigger would be far too eager.
	/// </summary>
	private static bool IsGitTreeMarker(string path)
	{
		var name = Path.GetFileName(path);
		return name.Equals("HEAD", StringComparison.Ordinal) || name.Equals("index", StringComparison.Ordinal);
	}

	private static string? FindGitDirectory(string start)
	{
		var directory = new DirectoryInfo(start);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, ".git");
			if (Directory.Exists(candidate)) return candidate;

			directory = directory.Parent;
		}

		return null;
	}

	public void Dispose() => _watcher?.Dispose();
}
