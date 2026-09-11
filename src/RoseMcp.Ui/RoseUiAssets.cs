namespace RoseMcp.Ui;

/// <summary>
/// Where this library's asset files land in an app that references it, and the names of the ones
/// there are.
/// <para>
/// It exists because the answer is not the obvious one and no app should have to know it. Content on
/// a referenced project is copied into the referencing app's output under a folder named for the
/// project, so the icon is at <c>RoseMcp.Ui/Assets/</c> beside the exe rather than <c>Assets/</c>.
/// Neither <c>Link</c> nor <c>TargetPath</c> on the app's own item moves it, and an app that spelled
/// the path itself would be encoding that surprise in as many places as there are apps.
/// </para>
/// </summary>
public static class RoseUiAssets
{
	/// <summary>
	/// A multi-frame icon with a purpose-drawn image at each size. Ask for the frame you want rather
	/// than letting a decoder choose: a taskbar scaling a 16px frame up looks like a smudge.
	/// </summary>
	public const string IconFile = "rose-mcp.ico";

	/// <summary>
	/// The same art as a single-frame PNG, for the marks drawn inside a window. An image decoder
	/// handed a multi-frame .ico picks its own frame, which is why this exists beside the icon.
	/// </summary>
	public const string MarkFile = "rose-mcp.png";

	/// <summary>
	/// The inspector's icon: the same monogram with a lens where the rose goes.
	/// <para>
	/// Its own mark because the two run side by side and the taskbar is where somebody picks between
	/// them. The composition is shared so they read as one family, and what differs is the one thing
	/// that says which is which.
	/// </para>
	/// </summary>
	public const string InspectorIconFile = "rose-inspector.ico";

	/// <summary>The inspector's mark as a single-frame PNG; see <see cref="MarkFile"/>.</summary>
	public const string InspectorMarkFile = "rose-inspector.png";

	/// <summary>The directory this library's assets are copied to, beside the running app's exe.</summary>
	public static string Directory => Path.Combine(AppContext.BaseDirectory, "RoseMcp.Ui", "Assets");

	/// <summary>
	/// The full path of one asset by file name. Not checked for existence; callers decide what a
	/// missing asset means, and for an icon the answer is to carry on without one.
	/// </summary>
	public static string For(string fileName) => Path.Combine(Directory, fileName);
}
