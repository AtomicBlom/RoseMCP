using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// The first view: a card for each agent session, what it is using, and its operations. Everything on it comes
/// from the window's feed -- the client table joined to the newest of the activity record -- so it asks the broker
/// nothing of its own.
/// </summary>
public sealed partial class AgentsView : UserControl
{
	private readonly ObservableCollection<AgentCard> _cards = [];
	private RoseFeed? _feed;
	private RoseWindow? _window;
	private bool _showing;

	public AgentsView()
	{
		InitializeComponent();
		Cards.ItemsSource = _cards;
		Empty.Text = RoseText.NoAgents;
	}

	/// <summary>Gives the view the feed it draws from and the window it navigates through.</summary>
	public void Attach(RoseFeed feed, RoseWindow window)
	{
		_feed = feed;
		_window = window;
		feed.Changed += Refresh;
	}

	/// <summary>Whether the view is on screen. One that is not does not rebuild its cards on every call an agent makes.</summary>
	public void Showing(bool visible)
	{
		_showing = visible;
		if (visible) Refresh();
	}

	/// <summary>Moves idle times and running durations on.</summary>
	public void Tick(DateTimeOffset now)
	{
		if (!_showing) return;

		foreach (var card in _cards) card.Tick(now);
	}

	private void Refresh()
	{
		if (!_showing || _feed is not { } feed) return;

		var now = DateTimeOffset.UtcNow;
		var clients = AgentCard.Order(feed.Clients.Clients);
		var entries = feed.Activity.Entries;
		var context = feed.Context;

		Rows.Merge(
			_cards,
			clients,
			card => card.Id,
			client => client.Id,
			client => new AgentCard(client, AgentCard.EntriesOf(client, entries), context, now),
			(card, client) => card.Update(client, AgentCard.EntriesOf(client, entries), context, now));

		foreach (var card in _cards) OperationView.LoadBodies(card.Operations);

		Summary.Text = AgentCard.Summarise(feed.Clients.Clients);
		Empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OnChip(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<UsingChip>(sender) is not { } chip || _window is not { } window) return;

		if (chip.IsWorkspace) window.ShowWorkspace(chip.Key);
		else window.OpenSession(chip.Key);
	}

	private void OnMore(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<AgentCard>(sender) is { } card) _window?.ShowActivityOf(card.ActivityFilter, card.Title);
	}

	private void OnDebugAnApp(object sender, RoutedEventArgs args) => _window?.DebugAnApp();
}
