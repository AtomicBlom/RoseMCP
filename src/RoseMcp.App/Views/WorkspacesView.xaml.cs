using System.Collections.ObjectModel;
using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RoseMcp.Settings;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// The machine's workspaces: what is loaded, for whom, under what, whether its answers can be trusted, what it costs
/// and how long it has gone unused -- and, opened, why, with what the person can do about it.
/// <para>
/// The rows come from the window's feed. What an opened row adds -- the person's acceptances, the agents'
/// proposals, the full status -- is read when it is opened or asked for, never polled: each is a question a person
/// brings to one workspace, and reading every workspace's full status every second would be the window's whole cost.
/// Reload and Close are asked first, because each stops a worker every agent using it shares.
/// </para>
/// </summary>
public sealed partial class WorkspacesView : UserControl
{
	private readonly ObservableCollection<WorkspaceRow> _rows = [];
	private RoseFeed? _feed;
	private OperatorClient? _client;
	private bool _showing;

	public WorkspacesView()
	{
		InitializeComponent();
		Rows.ItemsSource = _rows;
		Empty.Text = RoseText.NoWorkspaces;
	}

	/// <summary>Gives the view the feed its rows come from and the client its actions go through.</summary>
	public void Attach(RoseFeed feed, OperatorClient client)
	{
		_feed = feed;
		_client = client;
		feed.Changed += Refresh;
		feed.MemoryChanged += Refresh;
	}

	/// <summary>Whether the view is on screen. One that is not does not rebuild its rows on every call an agent makes.</summary>
	public void Showing(bool visible)
	{
		_showing = visible;
		if (visible) Refresh();
	}

	/// <summary>Moves unused times and close countdowns on.</summary>
	public void Tick(DateTime utcNow)
	{
		if (!_showing) return;

		foreach (var row in _rows) row.Tick(utcNow);
	}

	/// <summary>Opens the row for a solution, from a chip that named it elsewhere.</summary>
	public void Open(string solutionPath)
	{
		Refresh();

		var row = _rows.FirstOrDefault(row => Solutions.PathCasing.Comparer.Equals(row.Path, solutionPath));
		if (row is not null && !row.IsExpanded) _ = ExpandAsync(row);
	}

	private void Refresh()
	{
		if (!_showing || _feed is not { } feed) return;

		WorkspaceRow.Merge(_rows, feed.Workspaces.Workspaces, feed.Activity.Entries, DateTime.UtcNow);
		if (feed.Memory is { } sample)
		{
			foreach (var row in _rows) row.ShowMemory(sample);
		}

		var memory = feed.Memory is { } sampled
			? sampled.Workers.Sum(worker => worker.Process?.WorkingSetBytes ?? 0)
			: feed.Workspaces.Workspaces.Sum(workspace => workspace.Summary.WorkingSetBytes ?? 0);
		var loaded = feed.Workspaces.Workspaces.Count(workspace => workspace.Summary.Alive);
		var closes = feed.Workspaces.IdleEvictionAfter is { } after
			? $"each closes after {Format.Uptime(after)} unused"
			: "none closes by itself";

		Summary.Text = $"{loaded} loaded{Format.Separator}{Format.Bytes(memory)}{Format.Separator}{closes}";
		Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private async void OnToggle(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { } row) return;

		if (row.IsExpanded)
		{
			row.IsExpanded = false;
			return;
		}

		await ExpandAsync(row);
	}

	/// <summary>Opens a row and reads what the person has decided about it, and what agents proposed.</summary>
	private async Task ExpandAsync(WorkspaceRow row)
	{
		row.IsExpanded = true;
		await ActAsync(row, async client => row.ShowDecisions(
			await client.AcceptedAsync(row.Path, CancellationToken.None),
			await client.ProposalsAsync(CancellationToken.None)));
	}

	private async void OnShowLoad(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { } row) return;

		await ActAsync(row, async client => row.ShowLoad(await client.WorkspaceStatusAsync(row.Path, CancellationToken.None)));
	}

	private async void OnAccept(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<ReasonRow>(sender) is not { } reason || RowHolding(reason) is not { } row) return;

		await ActAsync(row, async client =>
		{
			var accepted = await client.AcceptAsync(reason.Accept(row.Path), CancellationToken.None);
			row.ShowDecisions(accepted, await client.ProposalsAsync(CancellationToken.None));
		});
	}

	private async void OnStopAccepting(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<ReasonRow>(sender) is not { } reason || RowHolding(reason) is not { } row) return;

		await ActAsync(row, async client =>
		{
			var accepted = await client.StopAcceptingAsync(reason.StopAccepting(row.Path), CancellationToken.None);
			row.ShowDecisions(accepted, await client.ProposalsAsync(CancellationToken.None));
		});
	}

	private async void OnStopAcceptingAccepted(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<AcceptedRow>(sender) is not { } accepted) return;

		var row = _rows.FirstOrDefault(row => row.Accepted.Contains(accepted));
		if (row is null) return;

		await ActAsync(row, async client =>
		{
			var now = await client.StopAcceptingAsync(accepted.StopAccepting(row.Path), CancellationToken.None);
			row.ShowDecisions(now, await client.ProposalsAsync(CancellationToken.None));
		});
	}

	private async void OnConfirm(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<ProposalRow>(sender) is not { } proposal) return;

		var row = _rows.FirstOrDefault(row => row.Proposals.Contains(proposal));
		if (row is null) return;

		await ActAsync(row, async client =>
		{
			var accepted = await client.ConfirmProposalAsync(proposal.Id, request: null, CancellationToken.None);
			row.ShowDecisions(accepted, await client.ProposalsAsync(CancellationToken.None));
		});
	}

	private async void OnReject(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<ProposalRow>(sender) is not { } proposal) return;

		var row = _rows.FirstOrDefault(row => row.Proposals.Contains(proposal));
		if (row is null) return;

		await ActAsync(row, async client =>
		{
			await client.RejectProposalAsync(proposal.Id, CancellationToken.None);
			row.ShowDecisions(await client.AcceptedAsync(row.Path, CancellationToken.None), await client.ProposalsAsync(CancellationToken.None));
		});
	}

	private async void OnReload(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { } row) return;

		var confirmed = await ConfirmAsync(
			$"Reload {row.Solution}?",
			$"Its worker stops and a fresh one loads the solution again, under the build it was last loaded with. "
				+ $"Every agent using it ({row.UsedBy}) waits for the load. A rebuilt analyzer is picked up this way.",
			"Reload");
		if (!confirmed) return;

		await ActAsync(row, async client =>
		{
			await client.ReloadWorkspaceAsync(row.Path, CancellationToken.None);
			_feed?.Kick();
		});
	}

	private async void OnClose(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { } row) return;

		var confirmed = await ConfirmAsync(
			$"Close {row.Solution}?",
			$"Its worker stops and its memory is given back. Every agent using it ({row.UsedBy}) loses it, and "
				+ "the next call that needs it loads it again from scratch.",
			"Close");
		if (!confirmed) return;

		await ActAsync(row, async client =>
		{
			await client.CloseWorkspaceAsync(row.Path, CancellationToken.None);
			_feed?.Kick();
		});
	}

	private void OnOpenFolder(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { } row) return;

		Shell(row, row.Folder, select: false);
	}

	/// <summary>Opens the worker's own log file, selected in Explorer: the file, not the folder of every worker's logs.</summary>
	private void OnOpenLog(object sender, RoutedEventArgs args)
	{
		if (RowOf(sender) is not { LogPath: { } log } row) return;

		Shell(row, log, select: true);
	}

	private void OnReloadWhenIdleLoaded(object sender, RoutedEventArgs args)
	{
		if (sender is CheckBox box) box.IsChecked = RoseSettingsFile.Read().ReloadRebuiltAnalyzersWhenIdle;
	}

	/// <summary>
	/// Turns reloading an idle workspace with rebuilt analyzers on or off for every workspace on the machine, written
	/// read-modify-write so it keeps what other windows wrote, and shown as the file then says.
	/// </summary>
	private void OnReloadWhenIdle(object sender, RoutedEventArgs args)
	{
		if (sender is not CheckBox box) return;

		var wanted = box.IsChecked == true;
		RoseSettingsFile.Write(RoseSettingsFile.Read() with { ReloadRebuiltAnalyzersWhenIdle = wanted });
		box.IsChecked = RoseSettingsFile.Read().ReloadRebuiltAnalyzersWhenIdle;
	}

	/// <summary>
	/// Runs something the person asked of a row, with the row busy meanwhile, and says on the row what went wrong
	/// where something did -- the broker's own sentence, which says what to do about it.
	/// </summary>
	private async Task ActAsync(WorkspaceRow row, Func<OperatorClient, Task> act)
	{
		if (_client is not { } client) return;

		row.IsBusy = true;
		row.Problem = string.Empty;

		try
		{
			await act(client);
		}
		catch (OperatorException exception)
		{
			row.Problem = exception.Message;
		}
		finally
		{
			row.IsBusy = false;
		}
	}

	private async Task<bool> ConfirmAsync(string title, string detail, string action)
	{
		var dialog = new ContentDialog
		{
			Title = title,
			Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
			PrimaryButtonText = action,
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = XamlRoot,
		};

		return await dialog.ShowAsync() == ContentDialogResult.Primary;
	}

	private static void Shell(WorkspaceRow row, string path, bool select)
	{
		try
		{
			var arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"";
			using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			row.Problem = $"Could not open {path}: {exception.Message}";
		}
	}

	private static WorkspaceRow? RowOf(object sender) => Bound.Item<WorkspaceRow>(sender);

	private WorkspaceRow? RowHolding(ReasonRow reason) => _rows.FirstOrDefault(row => row.Reasons.Contains(reason));
}
