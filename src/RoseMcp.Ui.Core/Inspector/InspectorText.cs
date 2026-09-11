namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Every sentence the inspector says when it has nothing to show, in one place.
/// <para>
/// Gathered rather than written into each pane because an empty state is the part of a window
/// people actually read, and it is the part that goes stale silently: a message naming a port, a
/// tray menu item or a tool that has been renamed is worse than no message, and nothing about a
/// blank pane makes anyone go looking. Here they can be read together and tested.
/// </para>
/// <para>
/// Each one says what happened and what to do about it. "Could not connect" is a fact about the
/// program; "start the tray and open the inspector from it" is what the reader wanted.
/// </para>
/// </summary>
public static class InspectorText
{
	/// <summary>The tray is not running, or is not listening where this inspector was told to look.</summary>
	public static string NoTray(Uri baseAddress) =>
		$"No tray is listening on {baseAddress.GetLeftPart(UriPartial.Authority)}. Start RoseMCP.Tray and "
			+ "open the inspector from it. This window keeps trying, and fills in when the tray answers.";

	/// <summary>
	/// The token is wrong. Almost always because the tray restarted: it mints a new one per run, so
	/// an inspector left open across a restart is holding yesterday's.
	/// </summary>
	public const string TokenRefused =
		"The tray refused this inspector's token. It changes each time the tray starts, so an "
			+ "inspector left open across a restart holds an old one. Open the inspector from the tray again.";

	/// <summary>Started by hand, with no token to present.</summary>
	public const string NoToken =
		"This inspector was started without a token, so the tray will not answer it. Use Inspect on a "
			+ "session in the tray, or Copy inspector command from its menu to get one.";

	/// <summary>The tray is answering and has no debug sessions.</summary>
	public const string NoSessions =
		"No debug sessions. An agent starts one with rose_debug_attach or rose_debug_launch, and it "
			+ "appears here within a second.";

	/// <summary>The session this window was opened on is not in the list any more.</summary>
	public static string SessionGone(string sessionId) =>
		$"Session {sessionId} is not there any more -- it was detached, or its host ended. Pick another "
			+ "from the list.";

	/// <summary>Waiting for a session the tray has not listed yet, which a fresh attach produces.</summary>
	public static string WaitingForSession(string sessionId) =>
		$"Waiting for session {sessionId} to appear.";

	/// <summary>Why the stack and thread panes are empty while the target runs.</summary>
	public const string NotStopped =
		"The target is running. Frames, variables and threads can only be read while it is held at a "
			+ "breakpoint or a step -- set one on the Breakpoints tab.";

	/// <summary>
	/// Shown over frames from a stop that has ended, rather than clearing them. A reader mid-scroll
	/// loses their place either way; leaving them there and saying they are old costs nothing.
	/// </summary>
	public const string StopEnded =
		"The target continued -- an agent, a step, or the safety timer. These frames are from the "
			+ "previous stop.";

	/// <summary>The event tail has nothing yet, which is ordinary for a session that just started.</summary>
	public const string NoEvents = "No events yet. The tail fills in as the target throws, hits or logs.";

	/// <summary>How many events the host dropped, which it does rather than growing without bound.</summary>
	public static string DroppedEvents(long dropped) =>
		dropped == 1
			? "1 earlier event was dropped by the host's buffer."
			: $"{dropped} earlier events were dropped by the host's buffer.";

	public const string NoBreakpoints =
		"No breakpoints. Add one by method name -- Namespace.Type.Method -- and it binds when the "
			+ "module carrying it is loaded.";

	public const string NoTracepoints =
		"No tracepoints. A tracepoint logs and lets the target run, so it is what to reach for when "
			+ "stopping the app would change what you are looking at.";

	/// <summary>Said while a tree read is outstanding, because a wedged app makes it a long wait.</summary>
	public const string ReadingTree =
		"Reading the visual tree. A busy or wedged app can take up to 30 seconds to answer.";

	/// <summary>
	/// Said under an element's properties, not used to filter them. Reading an element materialises
	/// its collection properties, so a later read reports as Local things the markup never set --
	/// and hiding that would hide exactly what an apply-then-read-back loop exists to verify.
	/// </summary>
	public static string PropertiesAreObserved(int reads) =>
		$"Reading an element's properties brings its collection properties into existence, so a later "
			+ $"read can list Local properties the markup never set. This element has been read {reads} time"
			+ (reads == 1 ? "." : "s.");
}
