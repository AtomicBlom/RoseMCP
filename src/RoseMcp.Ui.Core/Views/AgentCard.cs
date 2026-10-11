using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// One agent session as the Agents view shows it: what it is called, which branch it is on and how it reached Rose;
/// what it is using; and its operations, in the order every list of them keeps.
/// <para>
/// A card is one client of the broker. Two sessions in one folder are two relays and so two cards, which share the
/// workspace they use; a client that reached the broker over http without the relay is one card saying so, since
/// nothing tells the broker which session it is.
/// </para>
/// </summary>
public sealed class AgentCard : Observable
{
	/// <summary>How many operations a card lists. The rest are the Activity view's, filtered to this agent.</summary>
	public const int OperationLimit = 8;

	/// <summary>How long without a call before a card says the agent is idle.</summary>
	public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

	private string _title = string.Empty;
	private string _folder = string.Empty;
	private string _branch = string.Empty;
	private string _mode = string.Empty;
	private bool _isUnnamed;
	private bool _toolsStale;
	private bool _isDisconnected;
	private string _operationsHeader = string.Empty;
	private string _more = string.Empty;
	private DateTimeOffset? _lastCallUtc;
	private string _modeWithoutIdle = string.Empty;

	public AgentCard(ClientSummary client, IReadOnlyList<ActivityEntry> entries, AgentContext context, DateTimeOffset now)
	{
		Id = client.Id;

		Update(client, entries, context, now);
	}

	/// <summary>The client's row in the broker's table. Cards are matched on it, so they survive a refresh.</summary>
	public string Id { get; }

	/// <summary>The label it was started with, else the folder it works in, else what its client calls itself.</summary>
	public string Title
	{
		get => _title;
		private set => Set(ref _title, value);
	}

	/// <summary>The folder it works in, whole, where its relay said.</summary>
	public string Folder
	{
		get => _folder;
		private set
		{
			Set(ref _folder, value);
			OnPropertyChanged(nameof(HasFolder));
		}
	}

	public bool HasFolder => Folder.Length > 0;

	/// <summary>The branch its folder is on, where its relay said.</summary>
	public string Branch
	{
		get => _branch;
		private set
		{
			Set(ref _branch, value);
			OnPropertyChanged(nameof(HasBranch));
		}
	}

	public bool HasBranch => Branch.Length > 0;

	/// <summary>Its client and version, how it reached Rose, since when, and whether it has gone quiet.</summary>
	public string Mode
	{
		get => _mode;
		private set => Set(ref _mode, value);
	}

	/// <summary>Whether this is a client nothing names: one that reached the broker over http without the relay.</summary>
	public bool IsUnnamed
	{
		get => _isUnnamed;
		private set => Set(ref _isUnnamed, value);
	}

	/// <summary>
	/// Whether the client holds tools from before the broker restarted. Only a client without the relay can end up
	/// here: a relay refreshes its client's tools itself.
	/// </summary>
	public bool ToolsStale
	{
		get => _toolsStale;
		private set => Set(ref _toolsStale, value);
	}

	/// <summary>Whether the client's relay has let go of the broker, so the card is of a session that has gone.</summary>
	public bool IsDisconnected
	{
		get => _isDisconnected;
		private set => Set(ref _isDisconnected, value);
	}

	/// <summary>The workspaces and debugged apps it has called into, newest first.</summary>
	public ObservableCollection<UsingChip> Using { get; } = [];

	public bool IsUsingAnything => Using.Count > 0;

	/// <summary>Its operations, running first, then finished ones newest first.</summary>
	public ObservableCollection<OperationRow> Operations { get; } = [];

	public bool HasOperations => Operations.Count > 0;

	/// <summary>"Operations", or why there are none.</summary>
	public string OperationsHeader
	{
		get => _operationsHeader;
		private set => Set(ref _operationsHeader, value);
	}

	/// <summary>How many more the Activity view holds for this agent, where the card does not list them all.</summary>
	public string More
	{
		get => _more;
		private set
		{
			Set(ref _more, value);
			OnPropertyChanged(nameof(HasMore));
		}
	}

	public bool HasMore => More.Length > 0;

	/// <summary>What the Activity view filters on to show only this agent's calls: the key its calls carry where they carry one.</summary>
	public string ActivityFilter { get; private set; } = string.Empty;

	/// <summary>Brings the card in line with the client's row and the calls the record holds for it.</summary>
	public void Update(ClientSummary client, IReadOnlyList<ActivityEntry> entries, AgentContext context, DateTimeOffset now)
	{
		IsUnnamed = !client.Named;
		Title = client.Named ? AgentNames.Of(client) : "An HTTP client connected without the relay";
		Folder = client.WorkingDirectory ?? client.Roots.FirstOrDefault() ?? string.Empty;
		Branch = client.Branch ?? string.Empty;
		ToolsStale = client.ToolsCurrent == false;
		IsDisconnected = client.Watching == false;
		ActivityFilter = entries.Select(entry => entry.ClientKey).FirstOrDefault(key => key is { Length: > 0 }) ?? client.Id;

		_lastCallUtc = client.LastCallUtc;
		_modeWithoutIdle = DescribeMode(client);

		var shown = OperationOrder.Sort(entries).Take(OperationLimit).ToList();
		OperationOrder.Merge(Operations, shown, now);
		OnPropertyChanged(nameof(HasOperations));

		var calls = entries.Count(entry => entry.Kind == ActivityEntryKind.Call);
		OperationsHeader = calls == 0 ? "No calls in what the record holds" : "Operations";
		More = calls > shown.Count(entry => entry.Kind == ActivityEntryKind.Call)
			? $"{calls - shown.Count(entry => entry.Kind == ActivityEntryKind.Call)} more in Activity"
			: string.Empty;

		// Replaced rather than merged: a chip holds nothing a reader set, and its text changes with the state of
		// what it names.
		var chips = UsingChip.Of(entries, context);
		if (!Using.SequenceEqual(chips))
		{
			Using.Clear();
			foreach (var chip in chips) Using.Add(chip);
		}

		OnPropertyChanged(nameof(IsUsingAnything));

		Tick(now);
	}

	/// <summary>Moves the idle time and each running call's duration on, without asking the broker anything.</summary>
	public void Tick(DateTimeOffset now)
	{
		Mode = _lastCallUtc is { } last && now - last >= IdleAfter
			? $"{_modeWithoutIdle}{Format.Separator}idle {Format.Uptime(now - last)}"
			: _modeWithoutIdle;

		foreach (var operation in Operations) operation.Tick(now);
	}

	/// <summary>Its client and version, how it reached Rose, and since when.</summary>
	public static string DescribeMode(ClientSummary client)
	{
		var facts = new List<string>();

		var name = client.ClientName ?? client.ClientProcessName;
		if (name is { Length: > 0 }) facts.Add(client.ClientVersion is { Length: > 0 } version ? $"{name} {version}" : name);

		facts.Add(client.Named ? "through the relay" : "over http, without the relay");
		if (client.Watching == false) facts.Add("not connected");

		var since = client.ConnectedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
		facts.Add($"since {since}");

		if (client.ClientProcessId is { } pid) facts.Add(Format.Pid(pid));

		return string.Join(Format.Separator, facts);
	}

	/// <summary>
	/// The calls <paramref name="entries"/> hold for <paramref name="client"/>, and the broker starting between them.
	/// <para>
	/// By the client's row first, and then by the key those calls carry: a relay outlives a broker restart and keeps
	/// its session's key, where the row it gets from the new broker is new. So the calls an agent made before the
	/// restart are its calls too, and the restart sits among them as a marker where it happened.
	/// </para>
	/// </summary>
	public static IReadOnlyList<ActivityEntry> EntriesOf(ClientSummary client, IReadOnlyList<ActivityEntry> entries)
	{
		var keys = entries
			.Where(entry => entry.ClientId == client.Id && entry.ClientKey is { Length: > 0 })
			.Select(entry => entry.ClientKey!)
			.ToHashSet(StringComparer.Ordinal);

		var calls = entries
			.Where(entry => entry.Kind == ActivityEntryKind.Call
				&& (entry.ClientId == client.Id || (entry.ClientKey is { } key && keys.Contains(key))))
			.ToList();

		if (calls.Count == 0) return calls;

		// Only the starts that fall among this agent's calls: one before its first call says nothing about it.
		var first = calls.Min(entry => entry.StartedUtc);
		var starts = entries.Where(entry => entry.Kind == ActivityEntryKind.BrokerStarted && entry.StartedUtc > first);

		return [.. calls, .. starts];
	}

	/// <summary>
	/// The line under the Agents heading: how many sessions are connected, and whether every one holds this
	/// broker's tools -- only a client without the relay can be left holding the tools from before a restart.
	/// </summary>
	public static string Summarise(IReadOnlyList<ClientSummary> clients)
	{
		var connected = clients.Count(client => client.Watching != false);
		var stale = clients.Count(client => client.ToolsCurrent == false);

		var count = connected == 0 ? "No sessions connected" : $"{Format.Count(connected, "session")} connected";
		if (connected == 0) return count;

		return stale == 0
			? $"{count}{Format.Separator}all on this build"
			: $"{count}{Format.Separator}{Format.Count(stale, "session")} using tools from before the restart";
	}

	/// <summary>The order cards are shown in: connected sessions first, then the most recent caller first.</summary>
	public static IReadOnlyList<ClientSummary> Order(IEnumerable<ClientSummary> clients) =>
		[.. clients
			.OrderByDescending(client => client.Watching != false)
			.ThenByDescending(client => client.LastCallUtc ?? client.ConnectedUtc)
			.ThenBy(client => client.Id, StringComparer.Ordinal)];
}
