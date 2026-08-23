using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RoseMcp.Broker;

namespace RoseMcp.Tray;

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

		ApplyIcon();

		ShowCommand = new ShowWindowCommand(this);

		_timer = DispatcherQueue.CreateTimer();
		_timer.Interval = RefreshInterval;
		_timer.Tick += (_, _) => Refresh();
		_timer.Start();

		Refresh();
	}

	/// <summary>Bound to the tray icon's left click, which is the usual way back to the window.</summary>
	public ICommand ShowCommand { get; }

	/// <summary>
	/// Puts the same icon on the tray and the window.
	/// <para>
	/// Loaded from a real .ico rather than generated from text. The generated version depended on
	/// a font-size-to-canvas ratio that could not be checked without looking at the taskbar, and it
	/// went from a four-pixel smudge to nothing at all across one change. A file has pixels that can
	/// be verified before shipping.
	/// </para>
	/// </summary>
	private void ApplyIcon()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Assets", "rose-mcp.ico");
		if (!File.Exists(path)) return;

		try
		{
			// 32 rather than 16: the file has a purpose-drawn frame at each size, and asking for the
			// larger one means Windows only ever scales down, which is far kinder than scaling up on a
			// high-DPI taskbar.
			Tray.Icon = new System.Drawing.Icon(path, 32, 32);
			AppWindow.SetIcon(path);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			System.Diagnostics.Debug.WriteLine($"Could not apply the icon: {exception.Message}");
		}
	}

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
