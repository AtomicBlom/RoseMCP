using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RoslynMcp.Broker;

namespace RoslynMcp.Tray;

/// <summary>Lists what is loaded and what it costs.</summary>
public sealed partial class MainWindow : Window
{
	/// <summary>
	/// Slow enough not to be busywork, quick enough that memory visibly moves while a solution
	/// loads -- which is when someone is most likely to be watching.
	/// </summary>
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

	private readonly DispatcherQueueTimer _timer;
	private readonly App _app = (App)Application.Current;

	public MainWindow()
	{
		InitializeComponent();

		Endpoint.Text = $"http://{_app.Options.Host}:{_app.Options.Port}";

		ShowCommand = new ShowWindowCommand(this);

		_timer = DispatcherQueue.CreateTimer();
		_timer.Interval = RefreshInterval;
		_timer.Tick += (_, _) => Refresh();
		_timer.Start();

		Refresh();
	}

	/// <summary>Bound to the tray icon's left click, which is the usual way back to the window.</summary>
	public ICommand ShowCommand { get; }

	private WorkspaceManager Manager => _app.Services.GetRequiredService<WorkspaceManager>();

	private void Refresh()
	{
		var summaries = Manager.Describe();

		WorkspaceList.ItemsSource = summaries.Select(summary => new WorkspaceRow(summary)).ToArray();

		var totalBytes = summaries.Sum(summary => summary.WorkingSetBytes ?? 0);
		Summary.Text = summaries.Count == 0
			? "No solutions loaded. Point an MCP client at the endpoint above."
			: $"{summaries.Count} solution(s), {totalBytes / (1024.0 * 1024.0):F0} MB total working set.";
	}

	private async void OnReload(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string solutionPath }) return;

		await Manager.RestartAsync(solutionPath, CancellationToken.None);
		Refresh();
	}

	private async void OnClose(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string solutionPath }) return;

		await Manager.CloseAsync(solutionPath, CancellationToken.None);
		Refresh();
	}

	private void OnShow(object sender, RoutedEventArgs e) => Show();

	private async void OnCloseAll(object sender, RoutedEventArgs e)
	{
		foreach (var worker in Manager.Workers)
		{
			await Manager.CloseAsync(worker.SolutionPath, CancellationToken.None);
		}

		Refresh();
	}

	private async void OnExit(object sender, RoutedEventArgs e)
	{
		Tray.Dispose();
		await _app.ShutdownAsync();
	}

	/// <summary>
	/// Closing the window hides it rather than exiting. A tray app that quits when you close its
	/// window is not really a tray app, and quitting here would take every loaded solution with it.
	/// </summary>
	internal void Show()
	{
		AppWindow.Show();
		Activate();
	}

	private sealed class ShowWindowCommand(MainWindow window) : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter) => window.Show();
	}
}
