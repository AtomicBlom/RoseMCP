using System.Diagnostics;

using ModelContextProtocol;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// What every worker is doing, and what each one has just finished.
/// <para>
/// Lives in the broker rather than in the workers because the broker is the only party that sees
/// every call, including the ones a worker never gets to answer. It is also the only party still
/// around to say what happened when a worker dies mid-call.
/// </para>
/// </summary>
public sealed class ActivityLog
{
	/// <summary>
	/// Enough history to answer "what did that agent just do to my solution" and no more. This is
	/// live state for a UI, not an audit trail; logs are where a permanent record belongs.
	/// </summary>
	private const int RecentPerWorkspace = 8;

	private readonly object _gate = new();
	private readonly Dictionary<string, WorkspaceEntries> _byWorkspace = new(StringComparer.OrdinalIgnoreCase);
	private long _nextId;

	/// <summary>
	/// Starts tracking an operation. The returned scope is the only handle on it: reports go
	/// through it, and completing or disposing it moves the operation into the recent list.
	/// </summary>
	public ActivityScope Begin(
		string solutionPath,
		string operation,
		string? target = null,
		IProgress<ProgressNotificationValue>? upstream = null)
	{
		var tracked = new TrackedActivity(Interlocked.Increment(ref _nextId), operation, target);

		lock (_gate)
		{
			Entries(solutionPath).Running.Add(tracked);
		}

		return new ActivityScope(this, solutionPath, tracked, upstream);
	}

	/// <summary>Operations in flight for one workspace, oldest first.</summary>
	public IReadOnlyList<WorkerActivity> Running(string solutionPath)
	{
		lock (_gate)
		{
			return _byWorkspace.TryGetValue(solutionPath, out var entries)
				? [.. entries.Running.Select(activity => activity.Snapshot())]
				: [];
		}
	}

	/// <summary>Recently finished operations for one workspace, newest first.</summary>
	public IReadOnlyList<WorkerActivity> Recent(string solutionPath)
	{
		lock (_gate)
		{
			return _byWorkspace.TryGetValue(solutionPath, out var entries) ? [.. entries.Recent] : [];
		}
	}

	/// <summary>
	/// Drops everything known about a workspace. Called when one closes, so a reopened solution
	/// starts clean rather than inheriting the previous worker's history.
	/// </summary>
	public void Forget(string solutionPath)
	{
		lock (_gate)
		{
			_byWorkspace.Remove(solutionPath);
		}
	}

	private void MoveToRecent(string solutionPath, TrackedActivity activity)
	{
		lock (_gate)
		{
			var entries = Entries(solutionPath);
			entries.Running.Remove(activity);
			entries.Recent.Insert(0, activity.Snapshot());

			var surplus = entries.Recent.Count - RecentPerWorkspace;
			if (surplus > 0) entries.Recent.RemoveRange(RecentPerWorkspace, surplus);
		}
	}

	/// <summary>Only call while holding <see cref="_gate"/>.</summary>
	private WorkspaceEntries Entries(string solutionPath)
	{
		if (_byWorkspace.TryGetValue(solutionPath, out var existing)) return existing;

		return _byWorkspace[solutionPath] = new WorkspaceEntries();
	}

	private sealed class WorkspaceEntries
	{
		public List<TrackedActivity> Running { get; } = [];

		public List<WorkerActivity> Recent { get; } = [];
	}

	/// <summary>
	/// The mutable half of an activity, with its own lock because progress arrives on the client's
	/// notification thread while the UI is reading. Only immutable snapshots leave the log, so a
	/// caller can never hold a row that changes underneath it as it renders.
	/// </summary>
	internal sealed class TrackedActivity(long id, string operation, string? target)
	{
		private readonly object _gate = new();
		private readonly DateTime _startedUtc = DateTime.UtcNow;
		private readonly Stopwatch _elapsed = Stopwatch.StartNew();

		private string? _message;
		private double? _percentComplete;
		private ActivityOutcome _outcome = ActivityOutcome.Running;
		private string? _error;

		public void Update(string? message, double? percentComplete)
		{
			lock (_gate)
			{
				// A report with no message keeps the previous one. Progress that moves only the
				// number should not blank out the words explaining what the number means.
				if (!string.IsNullOrWhiteSpace(message)) _message = message;

				// A percentage, on the other hand, is replaced even by nothing: the sender saying it
				// no longer knows how far along it is must clear the bar, not leave it frozen at a
				// number that has stopped meaning anything.
				_percentComplete = percentComplete;
			}
		}

		/// <summary>True the first time only, so the first outcome recorded is the one that sticks.</summary>
		public bool Finish(ActivityOutcome outcome, string? error)
		{
			lock (_gate)
			{
				if (_outcome != ActivityOutcome.Running) return false;

				_elapsed.Stop();
				_outcome = outcome;
				_error = error;

				return true;
			}
		}

		public WorkerActivity Snapshot()
		{
			lock (_gate)
			{
				return new WorkerActivity
				{
					Id = id,
					Operation = operation,
					Target = target,
					StartedUtc = _startedUtc,
					Elapsed = _elapsed.Elapsed,
					Outcome = _outcome,
					Message = _message,
					PercentComplete = _percentComplete,
					Error = _error,
				};
			}
		}
	}

	/// <summary>
	/// A running operation's handle. Doubles as the progress sink handed to the worker, so
	/// everything the worker says about a call lands on the row describing that call -- and, when
	/// the client asked for progress itself, is passed on unchanged.
	/// </summary>
	public sealed class ActivityScope : IProgress<ProgressNotificationValue>, IDisposable
	{
		private readonly ActivityLog _log;
		private readonly string _solutionPath;
		private readonly TrackedActivity _activity;
		private readonly IProgress<ProgressNotificationValue>? _upstream;

		internal ActivityScope(
			ActivityLog log,
			string solutionPath,
			TrackedActivity activity,
			IProgress<ProgressNotificationValue>? upstream)
		{
			_log = log;
			_solutionPath = solutionPath;
			_activity = activity;
			_upstream = upstream;
		}

		public void Report(ProgressNotificationValue value)
		{
			_activity.Update(value.Message, Percent(value));
			_upstream?.Report(value);
		}

		public void Complete(ActivityOutcome outcome, string? error = null)
		{
			if (_activity.Finish(outcome, error)) _log.MoveToRecent(_solutionPath, _activity);
		}

		/// <summary>Succeeded unless something already said otherwise, which makes the happy path a using.</summary>
		public void Dispose() => Complete(ActivityOutcome.Succeeded);

		/// <summary>
		/// A missing total means the sender does not know how much work there is, which is not the
		/// same as having made no progress. Reporting that as null keeps a bar from sitting at zero
		/// and reading as a hang.
		/// </summary>
		private static double? Percent(ProgressNotificationValue value) =>
			// Widened before dividing: the protocol carries these as floats, and float division turns
			// a clean 30 of 100 into 30.000002, which then shows up in a tooltip.
			value.Total is > 0 ? Math.Clamp((double)value.Progress / value.Total.Value * 100, 0, 100) : null;
	}
}
