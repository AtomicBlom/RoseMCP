using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;

using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace RoseMcp.Tray;

/// <summary>Shows what is loaded, what state it is in, what it is doing, and what it costs.</summary>
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

	/// <summary>Long enough to be seen; short enough that the button is itself again before the next click.</summary>
	private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

	private const string CopyGlyph = "\uE8C8";
	private const string CopiedGlyph = "\uE73E";

	// Logical pixels, scaled to the monitor before use. Room for two or three cards with an
	// operation running in each; the minimum keeps the header's two halves from colliding.
	private const int InitialWidth = 780;
	private const int InitialHeight = 620;
	private const int MinimumWidth = 560;
	private const int MinimumHeight = 400;

	/// <summary>
	/// Rows live here and are updated in place, so the list is only ever added to or removed from.
	/// Rebuilding it each refresh would restart every progress bar and close any open expander.
	/// </summary>
	private readonly ObservableCollection<WorkspaceRow> _rows = [];

	private readonly DispatcherQueueTimer _timer;
	private readonly App _app = (App)Application.Current;
	private readonly string _endpoint;
	private bool _exiting;

	public MainWindow()
	{
		InitializeComponent();

		_endpoint = $"http://{_app.Options.Host}:{_app.Options.Port}";
		EndpointText.Text = _endpoint;
		RegistrationText.Text = RegistrationCommand(_endpoint);
		Workspaces.ItemsSource = _rows;

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(TitleBarArea);
		ApplyIcon();
		ApplySize();

		AppWindow.Closing += OnClosing;

		ShowCommand = new ShowWindowCommand(this);

		_timer = DispatcherQueue.CreateTimer();
		_timer.Interval = IdleInterval;
		_timer.Tick += (_, _) => Refresh();
		_timer.Start();

		Refresh();
	}

	/// <summary>Bound to the tray icon's left click, which is the usual way back to the window.</summary>
	public ICommand ShowCommand { get; }

	private WorkspaceManager Manager => _app.Services.GetRequiredService<WorkspaceManager>();

	/// <summary>
	/// Puts the same icon on the tray, the window and the header.
	/// <para>
	/// Loaded from a real .ico rather than generated from text. The generated version depended on
	/// a font-size-to-canvas ratio that could not be checked without looking at the taskbar, and it
	/// went from a four-pixel smudge to nothing at all across one change. A file has pixels that can
	/// be verified before shipping. The marks inside the window come from a PNG of the same art,
	/// because an image decoder handed a multi-frame .ico picks its own frame.
	/// </para>
	/// </summary>
	private void ApplyIcon()
	{
		var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
		var icon = Path.Combine(assets, "rose-mcp.ico");
		var mark = Path.Combine(assets, "rose-mcp.png");

		try
		{
			if (File.Exists(icon))
			{
				// 32 rather than 16: the file has a purpose-drawn frame at each size, and asking for
				// the larger one means Windows only ever scales down, which is far kinder than scaling
				// up on a high-DPI taskbar.
				Tray.Icon = new System.Drawing.Icon(icon, 32, 32);
				AppWindow.SetIcon(icon);
			}

			if (File.Exists(mark))
			{
				var image = new BitmapImage(new Uri(mark));
				TitleMark.Source = image;
				EmptyMark.Source = image;
			}
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			Debug.WriteLine($"Could not apply the icon: {exception.Message}");
		}
	}

	/// <summary>
	/// WinUI's default window is sized for an application; this is a status panel. Sizes are in
	/// logical pixels and scaled here, because AppWindow works in physical ones.
	/// </summary>
	private void ApplySize()
	{
		var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

		AppWindow.Resize(new SizeInt32(Scale(InitialWidth), Scale(InitialHeight)));

		if (AppWindow.Presenter is OverlappedPresenter presenter)
		{
			presenter.PreferredMinimumWidth = Scale(MinimumWidth);
			presenter.PreferredMinimumHeight = Scale(MinimumHeight);
		}

		int Scale(int logical) => (int)Math.Round(logical * scale);
	}

	private void Refresh()
	{
		var summaries = Manager.Describe();

		MergeRows(summaries);

		var running = summaries.Sum(summary => summary.Running.Count);
		Headline.Text = DescribeHeadline(summaries);
		Subtitle.Text = DescribeSubtitle(summaries, running);
		Tray.ToolTipText = DescribeTooltip(summaries.Count, running);

		var empty = summaries.Count == 0;
		EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
		WorkspaceScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

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

	/// <summary>The one line to read: how many, and whether they are up yet.</summary>
	public static string DescribeHeadline(IReadOnlyList<WorkspaceSummary> summaries)
	{
		if (summaries.Count == 0) return "Nothing loaded";

		var solutions = Format.Count(summaries.Count, "solution");
		var allLoading = summaries.All(summary => summary.State == WorkspaceState.Loading);

		return allLoading ? $"Loading {solutions}" : $"{solutions} loaded";
	}

	/// <summary>What it costs, whether it is busy, and whether anything below needs a look.</summary>
	public static string DescribeSubtitle(IReadOnlyList<WorkspaceSummary> summaries, int running)
	{
		if (summaries.Count == 0) return "Waiting for a client to ask about one.";

		var workingSet = summaries.Sum(summary => summary.WorkingSetBytes ?? 0);
		var parts = new List<string>
		{
			$"{Format.Bytes(workingSet)} working set",
			running == 0 ? "idle" : $"{Format.Count(running, "operation")} running",
		};

		var troubled = summaries.Count(summary => summary.State is WorkspaceState.Degraded or WorkspaceState.Faulted);
		if (troubled > 0) parts.Add(troubled == 1 ? "1 needs attention" : $"{troubled} need attention");

		return string.Join(WorkspaceRow.Separator, parts);
	}

	/// <summary>
	/// Kept to a few words: this is read hovering over a 16-pixel icon, and it is the only view of
	/// the broker available without opening the window.
	/// </summary>
	public static string DescribeTooltip(int workspaces, int running)
	{
		if (workspaces == 0) return "RoseMCP - nothing loaded";

		var solutions = Format.Count(workspaces, "solution");

		return running == 0 ? $"RoseMCP - {solutions}, idle" : $"RoseMCP - {solutions}, {running} running";
	}

	/// <summary>How a Claude Code user points their agent at this broker over http.</summary>
	public static string RegistrationCommand(string endpoint) => $"claude mcp add --transport http rose {endpoint}";

	private static bool Same(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

	private async void OnReload(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		await Manager.RestartAsync(WorkspaceHints.From(solutionPath), CancellationToken.None);
		Refresh();
	}

	private async void OnClose(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		await Manager.CloseAsync(WorkspaceHints.From(solutionPath), CancellationToken.None);
		Refresh();
	}

	private void OnOpenFolder(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string solutionPath }) return;

		OpenInExplorer($"/select,\"{solutionPath}\"");
	}

	/// <summary>
	/// The folder above this component's: Server, Worker and Tray sit side by side under it, and
	/// the question that brings someone here is usually which of them has the answer.
	/// </summary>
	private void OnOpenLogs(object sender, RoutedEventArgs e)
	{
		var own = RoseLogFile.DirectoryFor("Tray");
		var root = Path.GetDirectoryName(own) ?? own;

		Directory.CreateDirectory(root);
		OpenInExplorer($"\"{root}\"");
	}

	/// <summary>
	/// Both menus carry the same toggle, and Windows owns the answer -- the key can be changed by
	/// anything, including a second copy of this app -- so it is read when the menu opens rather than
	/// cached.
	/// </summary>
	private void OnMenuOpening(object sender, object e)
	{
		var enabled = StartupRegistration.IsEnabled;
		var elsewhere = StartupRegistration.PointsElsewhere;

		foreach (var item in (ToggleMenuFlyoutItem?[])[TrayStartWithWindows, WindowStartWithWindows])
		{
			if (item is null) continue;

			item.IsChecked = enabled;

			// An install that has moved: Windows still starts the old copy, so saying "off" would be
			// a lie and saying "on" would point at the wrong exe.
			item.Text = elsewhere ? "Start with Windows (registered elsewhere)" : "Start with Windows";
		}
	}

	private void OnToggleStartWithWindows(object sender, RoutedEventArgs e)
	{
		// Off means off even when the registration belongs to another copy: the checkbox was showing
		// unchecked, so the click asks for on, and on means this executable.
		var wanted = !StartupRegistration.IsEnabled;

		if (StartupRegistration.Set(wanted)) return;

		// A toggle that silently does nothing is worse than one that is not offered, and the menu is
		// the only surface certain to be visible here -- this is reachable from the tray with the
		// window hidden -- so the menu carries the news. Reset when the menu next opens.
		if (sender is ToggleMenuFlyoutItem item)
		{
			item.IsChecked = StartupRegistration.IsEnabled;
			item.Text = "Start with Windows (Windows refused)";
		}
	}

	private void OnCopyEndpoint(object sender, RoutedEventArgs e) => Copy(_endpoint, EndpointCopyGlyph);

	private void OnCopyRegistration(object sender, RoutedEventArgs e) => Copy(RegistrationText.Text, RegistrationCopyGlyph);

	private void OnShow(object sender, RoutedEventArgs e) => Show();

	private async void OnCloseAll(object sender, RoutedEventArgs e)
	{
		foreach (var worker in Manager.Workers)
		{
			await Manager.CloseAsync(WorkspaceHints.From(worker.SolutionPath), CancellationToken.None);
		}

		Refresh();
	}

	private async void OnExit(object sender, RoutedEventArgs e)
	{
		_exiting = true;
		Tray.Dispose();
		await _app.ShutdownAsync();
	}

	/// <summary>
	/// Closing the window hides it. A tray app that quits when its window closes is not a tray app,
	/// and quitting here would take every loaded solution with it; Exit in the menu is the
	/// deliberate way out, and the only one.
	/// </summary>
	private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_exiting) return;

		args.Cancel = true;
		sender.Hide();
	}

	internal void Show()
	{
		AppWindow.Show();
		Activate();
	}

	/// <summary>
	/// Copies, and says so by turning the button's glyph into a tick for a moment. A clipboard has
	/// no other visible effect, and a button that does nothing visible reads as broken.
	/// </summary>
	private void Copy(string text, FontIcon glyph)
	{
		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);

		glyph.Glyph = CopiedGlyph;

		var revert = DispatcherQueue.CreateTimer();
		revert.Interval = CopiedFor;
		revert.IsRepeating = false;
		revert.Tick += (_, _) => glyph.Glyph = CopyGlyph;
		revert.Start();
	}

	private static void OpenInExplorer(string arguments)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			Debug.WriteLine($"Could not open Explorer: {exception.Message}");
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);

	private sealed class ShowWindowCommand(MainWindow window) : ICommand
	{
		public event EventHandler? CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter) => window.Show();
	}
}
