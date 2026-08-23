using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Tray;

/// <summary>Lists what is loaded, what it is doing, and what it costs.</summary>
public sealed partial class MainWindow : Window
{
	/// <summary>
	/// Quick enough that a progress bar moves and elapsed times tick over, which is the whole
	/// point of watching a load. Only used while something is actually in flight.
	/// </summary>
	private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(400);

	/// <summary>
	/// Slow enough not to be busywork. An idle broker has nothing to say beyond memory, and memory
	/// does not move on its own.
	/// </summary>
	private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Rows live here and are updated in place, so the list is only ever added to or removed from.
	/// Rebuilding it each refresh would restart every progress bar and close any open expander.
	/// </summary>
	private readonly ObservableCollection<WorkspaceRow> _rows = [];

	private readonly DispatcherQueueTimer _timer;
	private readonly App _app = (App)Application.Current;

	public MainWindow()
	{
		InitializeComponent();

		Endpoint.Text = $"http://{_app.Options.Host}:{_app.Options.Port}";
		WorkspaceList.ItemsSource = _rows;

		ApplyIcon();

		ShowCommand = new ShowWindowCommand(this);

		_timer = DispatcherQueue.CreateTimer();
		_timer.Interval = IdleInterval;
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

		MergeRows(summaries);

		var running = summaries.Sum(summary => summary.Running.Count);
		Summary.Text = Describe(summaries, running);
		Tray.ToolTipText = DescribeTooltip(summaries.Count, running);

		// Poll harder only while there is something to watch. Setting the interval restarts the
		// timer, so only do it when it actually changed.
		var interval = running > 0 ? ActiveInterval : IdleInterval;
		if (_timer.Interval != interval) _timer.Interval = interval;
	}

	/// <summary>Adds rows for new workspaces, updates the rest, and drops the ones that closed.</summary>
	private void MergeRows(IReadOnlyList<WorkspaceSummary> summaries)
	{
		for (var index = _rows.Count - 1; index >= 0; index--)
		{
			var stillOpen = summaries.Any(summary => Same(summary.SolutionPath, _rows[index].SolutionPath));
			if (!stillOpen) _rows.RemoveAt(index);
		}

		foreach (var summary in summaries)
		{
			var existing = _rows.FirstOrDefault(row => Same(row.SolutionPath, summary.SolutionPath));

			if (existing is null)
			{
				_rows.Add(new WorkspaceRow(summary));
				continue;
			}

			existing.Update(summary);
		}
	}

	public static string Describe(IReadOnlyList<WorkspaceSummary> summaries, int running)
	{
		if (summaries.Count == 0) return "No solutions loaded. Point an MCP client at the endpoint above.";

		var megabytes = summaries.Sum(summary => summary.WorkingSetBytes ?? 0) / (1024.0 * 1024.0);
		var work = running switch
		{
			0 => "idle",
			1 => "1 operation running",
			_ => $"{running} operations running",
		};

		return $"{summaries.Count} solution(s), {megabytes:F0} MB total working set - {work}";
	}

	/// <summary>
	/// Kept to a few words: this is read hovering over a 16-pixel icon, and it is the only view of
	/// the broker available without opening the window.
	/// </summary>
	public static string DescribeTooltip(int workspaces, int running)
	{
		if (workspaces == 0) return "Roslyn MCP - nothing loaded";

		var solutions = workspaces == 1 ? "1 solution" : $"{workspaces} solutions";

		return running == 0 ? $"Roslyn MCP - {solutions}, idle" : $"Roslyn MCP - {solutions}, {running} running";
	}

	private static bool Same(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
