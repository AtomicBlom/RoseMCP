using Microsoft.UI.Xaml;

using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>
/// One operation as the window shows it: what was asked for, how long it has been going, and how
/// far it has got. Formatting lives here rather than in XAML converters so it can be read without
/// standing up a UI.
/// </summary>
public sealed class ActivityRow : Observable
{
	/// <summary>An error long enough to reflow the window is not worth showing in full.</summary>
	private const int ErrorLimit = 120;

	private string _status = string.Empty;
	private string _elapsed = string.Empty;
	private double _percent;
	private bool _isIndeterminate;
	private Visibility _barVisibility = Visibility.Collapsed;

	public ActivityRow(WorkerActivity activity)
	{
		Id = activity.Id;
		Title = Describe(activity);

		Update(activity);
	}

	/// <summary>The broker's id for the operation. Rows are matched on it, so they survive a refresh.</summary>
	public long Id { get; }

	/// <summary>Fixed for the life of the row: what was asked for does not change mid-call.</summary>
	public string Title { get; }

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

	/// <summary>Hidden once the operation is over, since a full bar says nothing a time does not.</summary>
	public Visibility BarVisibility
	{
		get => _barVisibility;
		private set => Set(ref _barVisibility, value);
	}

	public void Update(WorkerActivity activity)
	{
		var running = activity.Outcome == ActivityOutcome.Running;

		Status = Summarise(activity);
		Elapsed = FormatDuration(activity.Elapsed);
		Percent = activity.PercentComplete ?? 0;
		IsIndeterminate = running && activity.PercentComplete is null;
		BarVisibility = running ? Visibility.Visible : Visibility.Collapsed;
	}

	public static string Describe(WorkerActivity activity) =>
		activity.Target is { Length: > 0 } target ? $"{activity.Operation} - {target}" : activity.Operation;

	/// <summary>
	/// What the operation has to say for itself. While it runs that is the worker's own last word;
	/// afterwards, whether it worked -- and if not, why not, which is the whole reason failures are
	/// kept around at all.
	/// </summary>
	public static string Summarise(WorkerActivity activity) => activity.Outcome switch
	{
		ActivityOutcome.Running => activity.Message is { Length: > 0 } message ? message : "working",
		ActivityOutcome.Succeeded => "done",
		ActivityOutcome.Cancelled => "cancelled",
		_ => $"failed: {Shorten(activity.Error ?? "no reason given")}",
	};

	/// <summary>
	/// Sub-second precision below a minute, because the interesting comparison for a warm call is
	/// against the tens of milliseconds it should have taken.
	/// </summary>
	public static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
		? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
		: $"{elapsed.TotalSeconds:0.0}s";

	private static string Shorten(string error)
	{
		var firstLine = error.Split('\n')[0].Trim();

		return firstLine.Length <= ErrorLimit ? firstLine : firstLine[..ErrorLimit] + "...";
	}
}
