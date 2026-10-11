using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

using Windows.ApplicationModel.DataTransfer;

namespace RoseMcp.App.Views;

/// <summary>
/// One operation as a line that opens: when, which tool, aimed at what, how it ended and how long it took, and,
/// opened, its arguments and response formatted and a write's diff. The body is read when the line is first opened,
/// through the app's client, and read again when a call that was running when it was opened has finished.
/// </summary>
public sealed partial class OperationView : UserControl
{
	public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
		nameof(Row), typeof(OperationRow), typeof(OperationView), new PropertyMetadata(null));

	public static readonly DependencyProperty ShowWhereProperty = DependencyProperty.Register(
		nameof(ShowWhere), typeof(bool), typeof(OperationView), new PropertyMetadata(false));

	public OperationView() => InitializeComponent();

	/// <summary>The operation shown.</summary>
	public OperationRow? Row
	{
		get => (OperationRow?)GetValue(RowProperty);
		set => SetValue(RowProperty, value);
	}

	/// <summary>Whether the line names the workspace and the agent, which the Activity view needs and a card does not.</summary>
	public bool ShowWhere
	{
		get => (bool)GetValue(ShowWhereProperty);
		set => SetValue(ShowWhereProperty, value);
	}

	/// <summary>Reads a body every opened row in <paramref name="rows"/> lacks, as a list refresh leaves them.</summary>
	public static void LoadBodies(IEnumerable<OperationRow> rows)
	{
		foreach (var row in rows.Where(row => row.NeedsBody))
		{
			_ = row.LoadBodyAsync(Client.ActivityEntryAsync, CancellationToken.None);
		}
	}

	private static OperatorClient Client => ((App)Application.Current).Client;

	private async void OnToggle(object sender, RoutedEventArgs args)
	{
		if (Row is not { } row) return;

		try
		{
			await row.ToggleAsync(Client.ActivityEntryAsync, CancellationToken.None);
		}
		catch (OperationCanceledException)
		{
			// Nothing cancels this read but the app ending.
		}
	}

	private void OnCopyResponse(object sender, RoutedEventArgs args) => Copy(Row?.Response);

	private void OnCopyArguments(object sender, RoutedEventArgs args) => Copy(Row?.ArgumentsJson);

	/// <summary>Opens a file a write changed in whatever the person opens such a file with.</summary>
	private void OnOpenFile(object sender, RoutedEventArgs args)
	{
		if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path)) return;

		try
		{
			using var opened = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// No program is associated with the file's extension. Explorer, with the file selected, always opens.
			using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
		}
	}

	private static void Copy(string? text)
	{
		if (string.IsNullOrEmpty(text)) return;

		var package = new DataPackage();
		package.SetText(text);
		Clipboard.SetContent(package);
	}
}
