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
		"No breakpoints. Find a method above, then click the line to stop on; it binds when the module "
			+ "carrying it is loaded.";

	public const string NoTracepoints =
		"No tracepoints. A tracepoint logs and lets the target run, so it is what to reach for when "
			+ "stopping the app would change what you are looking at.";

	/// <summary>
	/// The picker before anything has been typed. It says what the box searches, because "the
	/// target's loaded modules" is not what a reader assumes: the answer covers the framework and
	/// every package as well as their own code.
	/// </summary>
	public const string FindAMethod =
		"Type part of a method or property name to search the target's loaded modules. Widget.Refresh "
			+ "narrows it; two characters is the shortest search.";

	/// <summary>A search that found nothing, which is the ordinary result of a name half-typed.</summary>
	public const string NoMethodsFound =
		"Nothing matches. The search covers the modules the target has loaded, so a method in code it "
			+ "has not reached yet is not there to find.";

	/// <summary>
	/// A method whose source is not on this machine. It is the ordinary case for anything out of a
	/// package, and the reader's next question is whether they can still break there -- so that is
	/// the second half of the sentence rather than a footnote.
	/// </summary>
	public const string NoSourceToPick =
		"There is no source here to pick a line from. Adding this sets a breakpoint at the method's "
			+ "first instruction, which is what a breakpoint on a name has always been.";

	/// <summary>Said beside the chosen position, so what will be added is legible before it is.</summary>
	public static string Chosen(string displayName, int line, string offset) =>
		$"{displayName} · line {line} · {offset}";

	/// <summary>Said when the method itself is what was chosen, with no position inside it.</summary>
	public static string ChosenMethod(string displayName) => $"{displayName} · its first instruction";

	/// <summary>Said while a tree read is outstanding, because a wedged app makes it a long wait.</summary>
	public const string ReadingTree =
		"Reading the visual tree. A busy or wedged app can take up to 30 seconds to answer.";

	/// <summary>
	/// Why the XAML tab does nothing while the debugger has the target stopped. Worth saying in full
	/// rather than greying the buttons out: "it is stopped" explains it, and the fix is two clicks
	/// away on another tab.
	/// </summary>
	public const string XamlNeedsARunningTarget =
		"The target is stopped, so it cannot answer about its visual tree: the XAML provider runs on the "
			+ "app's own UI thread and the debugger is holding it. Continue it from the Stack tab first.";

	/// <summary>Nothing is selected, which is the state the pane opens in.</summary>
	public const string NoElementSelected =
		"Pick an element in the tree, or use Pick from app and click one in the running app.";

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
