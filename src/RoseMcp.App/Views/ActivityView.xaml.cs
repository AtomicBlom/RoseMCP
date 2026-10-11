using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// Every call into Rose the activity record holds, across reloads and restarts, filtered by agent, workspace,
/// failure and text, in the order every list of operations keeps.
/// <para>
/// The newest page is held current by a long poll of its own, on the filter shown, and only while the view is on
/// screen; the pages behind it are read when asked for, with the cursor the last page gave.
/// </para>
/// </summary>
public sealed partial class ActivityView : UserControl
{
	/// <summary>How long typing has to pause before the search is sent, so each keystroke is not a question of its own.</summary>
	private static readonly TimeSpan SearchSettle = TimeSpan.FromMilliseconds(400);

	private readonly ActivityList _list = new();
	private readonly DispatcherQueueTimer _searchTimer;
	private IReadOnlyList<FilterChoice> _agents = [];
	private IReadOnlyList<FilterChoice> _workspaces = [];
	private ViewWatch<ActivityPage>? _watch;
	private RoseFeed? _feed;
	private OperatorClient? _client;
	private bool _choosing;
	private bool _showing;

	public ActivityView()
	{
		InitializeComponent();
		List.ItemsSource = _list.Rows;

		_searchTimer = DispatcherQueue.CreateTimer();
		_searchTimer.Interval = SearchSettle;
		_searchTimer.IsRepeating = false;
		_searchTimer.Tick += (_, _) => Ask();

		Render();
	}

	/// <summary>Gives the view the feed its filter choices come from and the client its pages are read through.</summary>
	public void Attach(RoseFeed feed, OperatorClient client)
	{
		_feed = feed;
		_client = client;
		feed.Changed += RefreshChoices;

		_watch = new ViewWatch<ActivityPage>(
			(after, wait, cancellationToken) => client.ActivityAsync(_list.Filter, before: null, after, wait, cancellationToken),
			page => page.Version,
			page =>
			{
				_list.ShowNewest(page, DateTimeOffset.UtcNow);
				Render();
			},
			exception => Footer.Text = exception.Message);
	}

	/// <summary>Whether the view is on screen. Its long poll runs only while it is.</summary>
	public void Showing(bool visible)
	{
		_showing = visible;

		if (visible)
		{
			RefreshChoices();
			_watch?.Start();
		}
		else
		{
			_watch?.Stop();
		}
	}

	/// <summary>Reads again at once, after a new token.</summary>
	public void Kick() => _watch?.Kick();

	/// <summary>Moves running durations on.</summary>
	public void Tick(DateTimeOffset now)
	{
		if (_showing) _list.Tick(now);
	}

	/// <summary>Narrows the list to one agent's calls, by the key they carry: what "more in Activity" on a card asks for.</summary>
	public void ShowAgent(string key, string label)
	{
		if (!_agents.Any(choice => choice.Value == key)) _agents = [.. _agents, new FilterChoice(label, key)];

		Fill(AgentChoice, _agents, key);
		Ask();
	}

	private void RefreshChoices()
	{
		if (!_showing || _feed is not { } feed) return;

		var agents = FilterChoice.Agents(feed.Activity.Entries);
		var workspaces = FilterChoice.Workspaces(feed.Workspaces.Workspaces, feed.Activity.Entries);

		// A choice the person made stays on offer even when the newest entries no longer name it.
		var agent = Chosen(AgentChoice, _agents);
		var workspace = Chosen(WorkspaceChoice, _workspaces);
		_agents = Keep(agents, _agents, agent);
		_workspaces = Keep(workspaces, _workspaces, workspace);

		Fill(AgentChoice, _agents, agent);
		Fill(WorkspaceChoice, _workspaces, workspace);

		AddressNotes();
	}

	private void Ask()
	{
		_searchTimer.Stop();

		_list.Ask(new ActivityFilter
		{
			Client = Chosen(AgentChoice, _agents),
			Workspace = Chosen(WorkspaceChoice, _workspaces),
			Failures = FailuresOnly.IsChecked == true,
			Text = string.IsNullOrWhiteSpace(Search.Text) ? null : Search.Text.Trim(),
		});

		Render();
		_watch?.Kick();
	}

	private void Render()
	{
		Header.Text = _list.Header;
		Footer.Text = _list.Footer;
		Older.Visibility = _list.HasOlder ? Visibility.Visible : Visibility.Collapsed;
		OpenRecord.Visibility = _list.Directory is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
		Empty.Text = _list.IsAnswered ? "Nothing the record holds matches." : "Reading the activity record…";
		Empty.Visibility = _list.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

		OperationView.LoadBodies(_list.Rows);
		AddressNotes();
	}

	/// <summary>Offers each call's note the session that made it, as the client table has it now.</summary>
	private void AddressNotes()
	{
		if (_feed is { } feed) NoteTargets.Address(_list.Rows, feed.Clients.Clients, feed.Activity.Entries);
	}

	private async void OnOlder(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _list.Next is not { } next) return;

		Reading.IsActive = true;
		Older.IsEnabled = false;

		try
		{
			var page = await client.ActivityAsync(_list.Filter, next, after: null, waitSeconds: 0, CancellationToken.None);
			_list.ShowOlder(page, DateTimeOffset.UtcNow);
			Render();
		}
		catch (OperatorException exception)
		{
			Footer.Text = exception.Message;
		}
		finally
		{
			Reading.IsActive = false;
			Older.IsEnabled = true;
		}
	}

	private void OnFilterChosen(object sender, SelectionChangedEventArgs args)
	{
		if (!_choosing) Ask();
	}

	private void OnFailuresToggled(object sender, RoutedEventArgs args) => Ask();

	private void OnSearchChanged(object sender, TextChangedEventArgs args)
	{
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private void OnOpenRecord(object sender, RoutedEventArgs args)
	{
		if (_list.Directory is not { Length: > 0 } folder || !Directory.Exists(folder)) return;

		using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
	}

	/// <summary>Fills a list of choices, selecting the one whose value is <paramref name="value"/>, without that counting as a choice made.</summary>
	private void Fill(ComboBox box, IReadOnlyList<FilterChoice> choices, string? value)
	{
		var labels = choices.Select(choice => choice.Label).ToList();
		var index = Math.Max(0, choices.ToList().FindIndex(choice => choice.Value == value));

		var unchanged = box.Items.Count == labels.Count && box.Items.Cast<object>().Select(item => item as string).SequenceEqual(labels);
		if (unchanged && box.SelectedIndex == index) return;

		_choosing = true;
		try
		{
			if (!unchanged)
			{
				box.Items.Clear();
				foreach (var label in labels) box.Items.Add(label);
			}

			box.SelectedIndex = index;
		}
		finally
		{
			_choosing = false;
		}
	}

	private static string? Chosen(ComboBox box, IReadOnlyList<FilterChoice> choices) =>
		box.SelectedIndex is var index && index >= 0 && index < choices.Count ? choices[index].Value : null;

	private static IReadOnlyList<FilterChoice> Keep(IReadOnlyList<FilterChoice> fresh, IReadOnlyList<FilterChoice> before, string? chosen)
	{
		if (chosen is null || fresh.Any(choice => choice.Value == chosen)) return fresh;

		return before.FirstOrDefault(choice => choice.Value == chosen) is { } kept ? [.. fresh, kept] : fresh;
	}
}
