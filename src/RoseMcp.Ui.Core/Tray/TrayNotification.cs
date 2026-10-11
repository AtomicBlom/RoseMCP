namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// One notification, worded: its title, its body, and what clicking it does.
/// <para>
/// Cut to the lengths a notification icon's balloon holds -- <c>NOTIFYICONDATA.szInfoTitle</c> is 64
/// characters and <c>szInfo</c> 256, terminators included -- so Windows never cuts a sentence somewhere
/// this did not choose.
/// </para>
/// </summary>
/// <param name="Kind">Which of the things worth interrupting for it is.</param>
/// <param name="Title">The first line, in bold.</param>
/// <param name="Body">The rest.</param>
/// <param name="Action">What clicking it does.</param>
public sealed record TrayNotification(TrayNotificationKind Kind, string Title, string Body, TrayAction Action)
{
	/// <summary>The longest title a balloon shows.</summary>
	public const int TitleLimit = 63;

	/// <summary>The longest body a balloon shows.</summary>
	public const int BodyLimit = 255;

	/// <summary>A newer release. Nothing is downloaded: what to do about one is the person's call.</summary>
	public static TrayNotification Release(string version) => Worded(
		TrayNotificationKind.Release,
		$"RoseMCP {version} is out",
		"Click to read about it. Nothing is downloaded or installed until you choose to.",
		TrayAction.OpenRelease);

	/// <summary>A failure Rose contained or could not, with where to read more.</summary>
	public static TrayNotification Failure(string message) => Worded(
		TrayNotificationKind.Failure,
		"Rose failed",
		$"{TrayStatus.Excerpt(message, 180)} Click to open its log.",
		TrayAction.OpenFailureLog);

	/// <summary>A broker that did not start, so nothing is serving, with where to read more.</summary>
	public static TrayNotification CouldNotStart(string reason) => Worded(
		TrayNotificationKind.Failure,
		"RoseMCP is not serving",
		$"It could not start: {TrayStatus.Excerpt(reason, 160)}. Click to open its log.",
		TrayAction.OpenFailureLog);

	/// <summary>
	/// An agent's answer to a note, which opens the RoseMCP app where the conversation is.
	/// </summary>
	/// <param name="agent">How the person knows the agent: its label, folder or client name.</param>
	/// <param name="answer">The start of what it said, or null to say only that it answered.</param>
	public static TrayNotification NoteAnswered(string agent, string? answer) => Worded(
		TrayNotificationKind.NoteAnswered,
		$"{TrayStatus.Excerpt(agent, 40)} answered your note",
		answer is { Length: > 0 } said ? TrayStatus.Excerpt(said, 200) : "Click to read it in RoseMCP.",
		TrayAction.OpenNoteAnswers);

	/// <summary>A notification with its title and body cut to what a balloon holds.</summary>
	public static TrayNotification Worded(TrayNotificationKind kind, string title, string body, TrayAction action) =>
		new(kind, TrayStatus.Excerpt(title, TitleLimit), Cut(body, BodyLimit), action);

	private static string Cut(string text, int limit) =>
		text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
}
