using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// The Activity view's list: every call the record holds that the filter matches, across restarts, in the order
/// every list of operations keeps, with a broker starting as a marker where it started.
/// <para>
/// The newest page is kept current by a long poll and the pages behind it are read on request with the page's
/// cursor; both are merged into one list, so an older call a reader opened stays open while new calls arrive above
/// it. A filter that changes forgets the older pages, which were answers to the old question.
/// </para>
/// </summary>
public sealed class ActivityList : Observable
{
	private readonly Dictionary<string, ActivityEntry> _older = new(StringComparer.Ordinal);
	private IReadOnlyList<ActivityEntry> _newest = [];
	private string _header = string.Empty;
	private string _footer = string.Empty;
	private string? _next;
	private bool _newestHasNext;
	private string? _directory;

	/// <summary>What the view asks the record for.</summary>
	public ActivityFilter Filter { get; private set; } = new() { Limit = PageSize };

	/// <summary>How many entries a page holds: enough to fill a tall window twice.</summary>
	public const int PageSize = 100;

	/// <summary>The rows, running first, then finished newest first.</summary>
	public ObservableCollection<OperationRow> Rows { get; } = [];

	public bool IsEmpty => Rows.Count == 0;

	/// <summary>How many calls match and how long they are kept.</summary>
	public string Header
	{
		get => _header;
		private set => Set(ref _header, value);
	}

	/// <summary>Whether there are older entries to read, or why the oldest are not listed.</summary>
	public string Footer
	{
		get => _footer;
		private set => Set(ref _footer, value);
	}

	/// <summary>Where the next older page starts, or null where nothing older matched.</summary>
	public string? Next
	{
		get => _next;
		private set
		{
			Set(ref _next, value);
			OnPropertyChanged(nameof(HasOlder));
		}
	}

	public bool HasOlder => Next is not null;

	/// <summary>The folder the record is kept in, so a person can find the files.</summary>
	public string? Directory
	{
		get => _directory;
		private set => Set(ref _directory, value);
	}

	/// <summary>Asks a different question: forgets the older pages, which answered the old one.</summary>
	public void Ask(ActivityFilter filter)
	{
		Filter = filter with { Limit = PageSize, Bodies = false };
		_older.Clear();
		_newest = [];
		Next = null;
		Rows.Clear();
		OnPropertyChanged(nameof(IsEmpty));
	}

	/// <summary>Shows the newest page, as the long poll answered it.</summary>
	public void ShowNewest(ActivityPage page, DateTimeOffset now)
	{
		_newest = page.Entries;
		_newestHasNext = page.Next is not null;

		// An older page already read reaches past the newest page's own cursor, so it keeps its own.
		if (_older.Count == 0) Next = page.Next;

		Header = Describe(page);
		Directory = page.Directory;
		Footer = DescribeEnd(page);
		Show(now);
	}

	/// <summary>Shows an older page, read with the cursor the last one gave.</summary>
	public void ShowOlder(ActivityPage page, DateTimeOffset now)
	{
		foreach (var entry in page.Entries) _older[entry.Id] = entry;

		Next = page.Next;
		Footer = DescribeEnd(page);
		Show(now);
	}

	/// <summary>Moves each running call's duration on.</summary>
	public void Tick(DateTimeOffset now)
	{
		foreach (var row in Rows) row.Tick(now);
	}

	private void Show(DateTimeOffset now)
	{
		// The newest page's copy wins over an older page's, since it is the later read of the same entry.
		var newest = _newest.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
		var entries = _newest.Concat(_older.Values.Where(entry => !newest.Contains(entry.Id)));

		OperationOrder.Merge(Rows, entries, now);
		OnPropertyChanged(nameof(IsEmpty));

		if (_older.Count == 0 && !_newestHasNext) Next = null;
	}

	private static string Describe(ActivityPage page)
	{
		var calls = page.Matched.ToString("N0", CultureInfo.InvariantCulture);
		var kept = page.Retention.TotalDays >= 1
			? $"kept for {Format.Count((int)Math.Round(page.Retention.TotalDays), "day")}"
			: $"kept for {Format.Uptime(page.Retention)}";

		return $"{calls} {(page.Matched == 1 ? "entry matches" : "entries match")}{Format.Separator}{kept}";
	}

	private static string DescribeEnd(ActivityPage page)
	{
		if (page.Next is not null) return string.Empty;

		return page.OlderNotListed
			? "Older entries are kept on disk past what a listing reads; each can still be found in the record's files."
			: "That is everything the record holds for this filter.";
	}
}
