using System.Runtime.InteropServices;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RoseMcp.Ui;

/// <summary>
/// The two things every RoseMCP window wants of the platform and neither XAML nor WinUI will do for
/// it: a size in logical pixels, and this product's icon.
/// </summary>
public static class WindowChrome
{
	/// <summary>
	/// Sizes a window in logical pixels and gives it a floor.
	/// <para>
	/// WinUI's default window is sized for an application, and these are panels. The arithmetic is
	/// here because <see cref="AppWindow"/> works in physical pixels, so every caller would otherwise
	/// repeat the same scaling and one of them would forget on a high-DPI monitor.
	/// </para>
	/// </summary>
	public static void ApplySize(Window window, int width, int height, int minimumWidth, int minimumHeight)
	{
		var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;

		window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Scale(width), Scale(height)));

		if (window.AppWindow.Presenter is OverlappedPresenter presenter)
		{
			presenter.PreferredMinimumWidth = Scale(minimumWidth);
			presenter.PreferredMinimumHeight = Scale(minimumHeight);
		}

		int Scale(int logical) => (int)Math.Round(logical * scale);
	}

	/// <summary>
	/// Puts this product's icon on the window and its art into whichever <see cref="Image"/> elements
	/// the caller draws marks with. Returns the icon's path when there was one, so a caller that also
	/// needs it -- a tray icon wants an <c>HICON</c> at a chosen frame size -- does not resolve it a
	/// second time.
	/// <para>
	/// A missing or unreadable asset leaves the window iconless rather than failing: an icon is the
	/// least important thing a window does, and throwing here would take down an app over it.
	/// </para>
	/// </summary>
	public static string? ApplyIcon(Window window, params Image?[] marks)
	{
		var icon = RoseUiAssets.For(RoseUiAssets.IconFile);
		var mark = RoseUiAssets.For(RoseUiAssets.MarkFile);

		try
		{
			var hasIcon = File.Exists(icon);
			if (hasIcon) window.AppWindow.SetIcon(icon);

			if (File.Exists(mark))
			{
				// One BitmapImage for every mark: they are the same pixels, and decoding once is both
				// less work and the only way they cannot disagree.
				var image = new BitmapImage(new Uri(mark));

				foreach (var element in marks)
				{
					if (element is not null) element.Source = image;
				}
			}

			return hasIcon ? icon : null;
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			System.Diagnostics.Debug.WriteLine($"Could not apply the icon: {exception.Message}");
			return null;
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);
}
