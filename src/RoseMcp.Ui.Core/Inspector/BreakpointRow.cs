using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One stopping breakpoint as the inspector shows it. Updated in place across a poll so the row a
/// reader is about to click does not move under them, and so a hit count that is climbing reads as
/// one number rising rather than a list rebuilding.
/// </summary>
public sealed class BreakpointRow : Observable
{
	private bool _bound;
	private string _boundLabel = string.Empty;
	private string _hits = string.Empty;
	private string _detail = string.Empty;
	private bool _hasDetail;
	private string _conditions = string.Empty;
	private bool _hasConditions;

	public BreakpointRow(LiveBreakpoint breakpoint)
	{
		Id = breakpoint.Id;
		Location = breakpoint.Location;

		Update(breakpoint);
	}

	/// <summary>The host's id, which identifies the row and is what removing it names.</summary>
	public string Id { get; }

	/// <summary>The method it was set on. A breakpoint cannot be moved, so this is fixed.</summary>
	public string Location { get; }

	/// <summary>
	/// Whether the debugger has actually bound it to code. An unbound breakpoint is not a failure --
	/// its module may simply not be loaded yet -- which is why it is a state and not an error.
	/// </summary>
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

	/// <summary>What the host said about it, which for an unbound one is why.</summary>
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

	/// <summary>The condition and auto-continue, in one line, so a row shows how it will behave.</summary>
	public string Conditions
	{
		get => _conditions;
		private set => Set(ref _conditions, value);
	}

	public bool HasConditions
	{
		get => _hasConditions;
		private set => Set(ref _hasConditions, value);
	}

	public void Update(LiveBreakpoint breakpoint)
	{
		Bound = breakpoint.Bound;
		BoundLabel = breakpoint.Bound ? "bound" : "not bound yet";
		Hits = Format.Count((int)Math.Min(breakpoint.HitCount, int.MaxValue), "hit");
		Detail = breakpoint.Detail ?? string.Empty;
		HasDetail = Detail.Length > 0;
		Conditions = DescribeConditions(breakpoint);
		HasConditions = Conditions.Length > 0;
	}

	/// <summary>
	/// How the breakpoint will behave when it is reached, in the order a reader asks: what has to be
	/// true, and how long the target is held.
	/// </summary>
	public static string DescribeConditions(LiveBreakpoint breakpoint)
	{
		var parts = new List<string>();

		if (!string.IsNullOrWhiteSpace(breakpoint.Condition)) parts.Add($"when {breakpoint.Condition}");

		parts.Add(breakpoint.AutoContinueSeconds > 0
			? $"auto-continues after {breakpoint.AutoContinueSeconds}s"
			: "held until continued");

		return string.Join(" · ", parts);
	}
}
