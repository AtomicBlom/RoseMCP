using System.Collections.ObjectModel;
using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Logging;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// Every debug session on the machine, whoever started it: what it is attached to, whether the app is running or
/// stopped, how old the host's report of it is, and its Inspector window, its log and Detach.
/// </summary>
public sealed partial class DebuggingView : UserControl
{
	private readonly ObservableCollection<SessionRow> _rows = [];
	private RoseFeed? _feed;
	private RoseWindow? _window;
	private OperatorClient? _client;
	private bool _showing;

	public DebuggingView()
	{
		InitializeComponent();
		Sessions.ItemsSource = _rows;
		Empty.Text = RoseText.NoSessions;
	}

	/// <summary>Gives the view the feed its sessions come from, the window it opens Inspectors through, and the client Detach goes through.</summary>
	public void Attach(RoseFeed feed, RoseWindow window, OperatorClient client)
	{
		_feed = feed;
		_window = window;
		_client = client;
		feed.Changed += Refresh;
	}

	/// <summary>Whether the view is on screen.</summary>
	public void Showing(bool visible)
	{
		_showing = visible;
		if (visible) Refresh();
	}

	/// <summary>Moves report ages, heartbeats and resume countdowns on.</summary>
	public void Tick(DateTime utcNow)
	{
		if (!_showing) return;

		foreach (var row in _rows) row.Tick(utcNow);
	}

	private void Refresh()
	{
		if (!_showing || _feed is not { } feed) return;

		var sessions = feed.Sessions.Sessions;

		Rows.Merge(
			_rows,
			sessions,
			row => row.SessionId,
			session => session.SessionId,
			session => new SessionRow(session),
			(row, session) => row.Update(session));

		var live = sessions.Count(session => session.State != Contracts.LiveAppSessionState.Ended);
		Summary.Text = live == 0 ? "Nothing is being debugged" : $"{Format.Count(live, "app")} being debugged";
		Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OnDebugAnApp(object sender, RoutedEventArgs args) => _window?.DebugAnApp();

	private void OnOpen(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<SessionRow>(sender) is { } row) _window?.OpenSession(row.SessionId, row.TargetProcessId);
	}

	/// <summary>Opens the session's own host log, selected in Explorer: its file, not the folder of every host's logs.</summary>
	private void OnOpenLog(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<SessionRow>(sender) is not { } row) return;

		try
		{
			if (row.HostLogPath is { } log && File.Exists(log))
			{
				using var selected = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log}\"") { UseShellExecute = true });
				return;
			}

			var folder = RoseLogFile.DirectoryFor("LiveApp");
			Directory.CreateDirectory(folder);
			using var opened = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
			Say("The host has not named its log, so this is the folder every host logs to.", InfoBarSeverity.Informational);
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			Say($"Could not open the log: {exception.Message}", InfoBarSeverity.Warning);
		}
	}

	private async void OnDetach(object sender, RoutedEventArgs args)
	{
		if (Bound.Item<SessionRow>(sender) is not { } row || _client is not { } client) return;

		var dialog = new ContentDialog
		{
			Title = $"Detach from {row.DisplayName}?",
			Content = new TextBlock
			{
				Text = "The session ends and the app keeps running. An agent using the session loses it, and its Inspector window says so.",
				TextWrapping = TextWrapping.Wrap,
			},
			PrimaryButtonText = "Detach",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = XamlRoot,
		};

		if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

		try
		{
			var closed = await client.DetachAsync(row.SessionId, CancellationToken.None);

			// Closed and still attached are different claims, and only the second is what detaching asked for.
			if (closed.DetachFailure is { } failure)
			{
				Say($"The session closed, but the debugger did not come off the app: {failure}", InfoBarSeverity.Error);
			}

			_feed?.Kick();
		}
		catch (OperatorException exception)
		{
			Say(exception.Message, InfoBarSeverity.Error);
		}
	}

	private void Say(string message, InfoBarSeverity severity)
	{
		Notice.Severity = severity;
		Notice.Message = message;
		Notice.IsOpen = true;
	}
}
