namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// How a wait for a debug event ended. Three answers rather than a bool, because "there is one now"
/// covers both a caller that waited for something to happen and a caller that was handed something
/// which had already happened -- and only the second is a reason to say anything to them.
/// </summary>
public enum DebugEventWait
{
	/// <summary>
	/// Something past the cursor already matched when the call arrived, so it did not wait at all.
	/// True of every wait whose cursor is older than the thing it is waiting for.
	/// </summary>
	AlreadyBuffered,

	/// <summary>Nothing matched on arrival, and something matching arrived before the deadline.</summary>
	Arrived,

	/// <summary>Nothing matched on arrival, and nothing matching arrived before the deadline.</summary>
	TimedOut,
}
