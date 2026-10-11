using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// The first view: a card for each agent session, what it is using, the notes the person sent it, and its operations,
/// with the notes to sessions that have gone after the cards. Everything on it comes from the window's feed -- the
/// client table joined to the newest of the activity record and to the notes -- so it asks the broker nothing of its
/// own.
/// </summary>
public sealed partial class AgentsView : UserControl
{
	private readonly ObservableCollection<AgentCard> _cards = [];
	private readonly ObservableCollection<NoteRow> _goneNotes = [];
	private RoseFeed? _feed;
	private RoseWindow? _window;
	private bool _showing;

	public AgentsView()
	{
		InitializeComponent();
		Cards.ItemsSource = _cards;
		GoneNotes.ItemsSource = _goneNotes;
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

		foreach (var card in _cards)
		{
			card.ShowNotes(feed.Notes.Notes);
			OperationView.LoadBodies(card.Operations);
		}

		var gone = AgentCard.GoneNotes(feed.Notes.Notes, feed.Clients.Clients);
		Rows.Merge(_goneNotes, gone, row => row.Id, note => note.Id, note => new NoteRow(note), (row, note) => row.Update(note));
		GoneCard.Visibility = _goneNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		Summary.Text = AgentCard.Summarise(feed.Clients.Clients);
		Empty.Visibility = _cards.Count == 0 && _goneNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

	/// <summary>Shows the Activity view narrowed to the calls of the session a note went to, which outlives its card.</summary>
	private void OnNoteCalls(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<NoteRow>(sender) is { } note) _window?.ShowActivityOf(note.ActivityFilter, note.Agent);
	}

	private void OnDebugAnApp(object sender, RoutedEventArgs args) => _window?.DebugAnApp();
}
