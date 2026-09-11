using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One tracepoint as the inspector shows it: a breakpoint that logs and lets the target run, so
/// what a reader wants from the row is how often it fires and what it writes.
/// </summary>
public sealed class TracepointRow : Observable
{
	private bool _bound;
	private string _boundLabel = string.Empty;
	private string _hits = string.Empty;
	private string _detail = string.Empty;
	private bool _hasDetail;
	private string _behaviour = string.Empty;

	private string _source = string.Empty;

	private bool _hasSource;

	public TracepointRow(LiveTracepoint tracepoint)
	{
		Id = tracepoint.Id;
		Location = tracepoint.Location;

		Update(tracepoint);
	}

	public string Id { get; }

	public string Location { get; }

	public bool Bound
	{
		get => _bound;
		private set => Set(ref _bound, value);
	}

	public string BoundLabel
	{
		get => _boundLabel;
		private set => Set(ref _boundLabel, value);
	}

	public string Hits
	{
		get => _hits;
		private set => Set(ref _hits, value);
	}

	public string Detail
	{
		get => _detail;
		private set => Set(ref _detail, value);
	}

	public bool HasDetail
	{
		get => _hasDetail;
		private set => Set(ref _hasDetail, value);
	}

	/// <summary>What it logs, how often, and under what condition, in one line.</summary>
	public string Behaviour
	{
		get => _behaviour;
		private set => Set(ref _behaviour, value);
	}

	/// <summary>
	/// Where it actually bound, as <c>Program.cs:42</c>. A location names a method and two overloads
	/// share one, so this is how somebody sees it went somewhere other than where they meant.
	/// </summary>
	public string Source
	{
		get => _source;
		private set => Set(ref _source, value);
	}

	public bool HasSource
	{
		get => _hasSource;
		private set => Set(ref _hasSource, value);
	}

	public void Update(LiveTracepoint tracepoint)
	{
		Bound = tracepoint.Bound;
		BoundLabel = tracepoint.Bound ? "bound" : "not bound yet";
		Hits = Format.Count((int)Math.Min(tracepoint.HitCount, int.MaxValue), "hit");
		Detail = tracepoint.Detail ?? string.Empty;
		HasDetail = Detail.Length > 0;
		Behaviour = DescribeBehaviour(tracepoint);
		Source = Format.FileLine(tracepoint.Source?.File, tracepoint.Source?.Line);
		HasSource = Source.Length > 0;
	}

	/// <summary>
	/// The three things that decide what appears in the tail. Every-Nth is said as "every 10th hit"
	/// rather than as a number on its own, because "10" beside a hit count reads as another count.
	/// </summary>
	public static string DescribeBehaviour(LiveTracepoint tracepoint)
	{
		var parts = new List<string>();

		if (!string.IsNullOrWhiteSpace(tracepoint.Condition)) parts.Add($"when {tracepoint.Condition}");

		if (tracepoint.LogEveryNthHit is > 1 and { } nth) parts.Add($"every {Ordinal(nth)} hit");

		parts.Add(string.IsNullOrWhiteSpace(tracepoint.LogMessage)
			? "logs the location"
			: $"logs \"{tracepoint.LogMessage}\"");

		return string.Join(" · ", parts);
	}

	/// <summary>2nd, 3rd, 11th. Enough of the rule to be right, which is all a label needs.</summary>
	public static string Ordinal(int value)
	{
		var suffix = (value % 100) is >= 11 and <= 13
			? "th"
			: (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

		return $"{value}{suffix}";
	}
}
