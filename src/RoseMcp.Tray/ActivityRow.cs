using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>
/// One operation as the window shows it: what was asked for, how long it has been going, how far
/// it has got, and -- once it is over -- how it ended. Formatting lives here rather than in XAML
/// converters so it can be read without standing up a UI.
/// </summary>
public sealed class ActivityRow : Observable
{
	/// <summary>An error long enough to reflow the window is not worth showing in full.</summary>
	private const int ErrorLimit = 240;

	private string _status = string.Empty;
	private string _elapsed = string.Empty;
	private double _percent;
	private bool _isIndeterminate;
	private bool _isRunning;
	private bool _succeeded;
	private bool _failed;
	private bool _cancelled;
	private string _error = string.Empty;
	private bool _hasError;

	public ActivityRow(WorkerActivity activity)
	{
		Id = activity.Id;
		Operation = activity.Operation;
		Label = Format.Humanise(activity.Operation);
		Target = activity.Target ?? string.Empty;
		HasTarget = Target.Length > 0;

		Update(activity);
	}

	/// <summary>The broker's id for the operation. Rows are matched on it, so they survive a refresh.</summary>
	public long Id { get; }

	/// <summary>The tool name exactly as the client sent it, for the tooltip.</summary>
	public string Operation { get; }

	/// <summary>Fixed for the life of the row: what was asked for does not change mid-call.</summary>
	public string Label { get; }

	/// <summary>What the operation is aimed at. Empty for the whole solution.</summary>
	public string Target { get; }

	public bool HasTarget { get; }

	/// <summary>The worker's last word on what it is doing, while it is doing it.</summary>
	public string Status
	{
		get => _status;
		private set => Set(ref _status, value);
	}

	public string Elapsed
	{
		get => _elapsed;
		private set => Set(ref _elapsed, value);
	}

	public double Percent
	{
		get => _percent;
		private set => Set(ref _percent, value);
	}

	/// <summary>
	/// True when the operation is running but cannot say how far along it is. A moving bar with no
	/// position is the honest picture; one sitting at zero reads as a hang.
	/// </summary>
	public bool IsIndeterminate
	{
		get => _isIndeterminate;
		private set => Set(ref _isIndeterminate, value);
	}

	public bool IsRunning
	{
		get => _isRunning;
		private set => Set(ref _isRunning, value);
	}

	public bool Succeeded
	{
		get => _succeeded;
		private set => Set(ref _succeeded, value);
	}

	public bool Failed
	{
		get => _failed;
		private set => Set(ref _failed, value);
	}

	public bool Cancelled
	{
		get => _cancelled;
		private set => Set(ref _cancelled, value);
	}

	/// <summary>Why it failed, which is the whole reason failures are kept around at all.</summary>
	public string Error
	{
		get => _error;
		private set => Set(ref _error, value);
	}

	public bool HasError
	{
		get => _hasError;
		private set => Set(ref _hasError, value);
	}

	public void Update(WorkerActivity activity)
	{
		IsRunning = activity.Outcome == ActivityOutcome.Running;
		Succeeded = activity.Outcome == ActivityOutcome.Succeeded;
		Failed = activity.Outcome == ActivityOutcome.Failed;
		Cancelled = activity.Outcome == ActivityOutcome.Cancelled;

		Status = Summarise(activity);
		Elapsed = Format.Duration(activity.Elapsed);
		Percent = activity.PercentComplete ?? 0;
		IsIndeterminate = IsRunning && activity.PercentComplete is null;
		Error = Failed ? Shorten(activity.Error ?? "no reason given") : string.Empty;
		HasError = Error.Length > 0;
	}

	public static string Summarise(WorkerActivity activity) => activity.Outcome switch
	{
		ActivityOutcome.Running => activity.Message is { Length: > 0 } message ? message : "working",
		ActivityOutcome.Succeeded => "done",
		ActivityOutcome.Cancelled => "cancelled",
		_ => "failed",
	};

	private static string Shorten(string error)
	{
		var firstLine = error.Split('\n')[0].Trim();

		return firstLine.Length <= ErrorLimit ? firstLine : firstLine[..ErrorLimit] + "...";
	}
}
