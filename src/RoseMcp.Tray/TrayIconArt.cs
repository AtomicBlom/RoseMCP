using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

using RoseMcp.Ui.Core.Tray;

namespace RoseMcp.Tray;

/// <summary>
/// The tray icon's three pictures, drawn once from the product's own icon: as it is for serving, with a
/// badge for needing the person, and greyed with a badge for not serving.
/// <para>
/// Drawn at start rather than shipped as three more <c>.ico</c> files, because the badge is the whole
/// difference between them and three files would be three copies of the rose for the icon generator to
/// keep in step. The 32-pixel frame is the one drawn on: the file has a purpose-drawn image at each size,
/// and starting from the larger means Windows only ever scales down, which is far kinder than scaling up
/// on a high-DPI taskbar.
/// </para>
/// </summary>
internal sealed class TrayIconArt : IDisposable
{
	private const int Size = 32;

	/// <summary>The badge's diameter, in pixels of the 32-pixel frame: large enough to read at 16.</summary>
	private const int BadgeSize = 14;

	private readonly Icon _serving;
	private readonly Icon _needsYou;
	private readonly Icon _notServing;

	public TrayIconArt(string iconPath)
	{
		using var source = Load(iconPath);
		using var art = source.ToBitmap();

		_serving = (Icon)source.Clone();
		_needsYou = Compose(art, grey: false, Color.FromArgb(0xF7, 0x63, 0x0C));
		_notServing = Compose(art, grey: true, Color.FromArgb(0xC4, 0x2B, 0x1C));
	}

	/// <summary>
	/// The picture for <paramref name="state"/>. Owned here and disposed with this object, so a caller that
	/// gives it to something that takes ownership -- the notification icon disposes each picture it is given
	/// once it is given the next -- gives it a clone.
	/// </summary>
	public Icon For(TrayIconState state) => state switch
	{
		TrayIconState.NeedsYou => _needsYou,
		TrayIconState.NotServing => _notServing,
		_ => _serving,
	};

	public void Dispose()
	{
		_serving.Dispose();
		_needsYou.Dispose();
		_notServing.Dispose();
	}

	/// <summary>
	/// The product icon's 32-pixel frame, or Windows' own application icon where the file is missing or
	/// unreadable: an icon is the least important thing about the tray, and the one thing it cannot be
	/// without, since an icon is all a person has to find it and exit it by.
	/// </summary>
	private static Icon Load(string path)
	{
		try
		{
			if (File.Exists(path)) return new Icon(path, Size, Size);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			Debug.WriteLine($"Could not read the tray icon, using Windows' own: {exception.Message}");
		}

		return (Icon)SystemIcons.Application.Clone();
	}

	/// <summary>The art, greyed and faded where asked, with a badge in the lower right ringed to stand off it.</summary>
	private static Icon Compose(Bitmap art, bool grey, Color badge)
	{
		using var canvas = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);

		using (var graphics = Graphics.FromImage(canvas))
		{
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

			using var attributes = new ImageAttributes();
			if (grey) attributes.SetColorMatrix(Greyed);

			graphics.DrawImage(art, new Rectangle(0, 0, Size, Size), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attributes);

			var bounds = new Rectangle(Size - BadgeSize - 1, Size - BadgeSize - 1, BadgeSize, BadgeSize);

			using var fill = new SolidBrush(badge);
			using var ring = new Pen(Color.White, 2f);
			graphics.FillEllipse(fill, bounds);
			graphics.DrawEllipse(ring, bounds);
		}

		var handle = canvas.GetHicon();

		try
		{
			// Cloned so the icon owns a copy of its handle: one made from a handle does not, and the
			// handle GetHicon made would otherwise outlive every icon drawn from it.
			using var borrowed = Icon.FromHandle(handle);
			return (Icon)borrowed.Clone();
		}
		finally
		{
			DestroyIcon(handle);
		}
	}

	/// <summary>Luminance into every channel, at half opacity: the rose still recognisable, plainly switched off.</summary>
	private static readonly ColorMatrix Greyed = new(
	[
		[0.299f, 0.299f, 0.299f, 0f, 0f],
		[0.587f, 0.587f, 0.587f, 0f, 0f],
		[0.114f, 0.114f, 0.114f, 0f, 0f],
		[0f, 0f, 0f, 0.5f, 0f],
		[0f, 0f, 0f, 0f, 1f],
	]);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(nint handle);
}
