using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One of a stopped target's managed threads, as the inspector shows it.
/// <para>
/// Built fresh per stop rather than updated in place, for the reason a <see cref="FrameRow"/> is: a
/// thread list is only answerable while the runtime is synchronized, so every row in it describes
/// one instant. Carrying a row across a resume would be a confident description of a thread that has
/// since run somewhere else.
/// </para>
/// </summary>
public sealed class ThreadRow
{
	public ThreadRow(LiveThread thread)
	{
		Id = thread.Id;
		IdLabel = thread.Id.ToString();
		IsStopped = thread.IsStopped;

		// Said rather than left blank, and in a shape no method name has. A thread with no managed
		// frame is ordinary -- the finalizer and the pool's waiters are always in that state -- and
		// an empty cell reads as a thread the debugger failed to walk.
		TopFrame = thread.TopFrame ?? "no managed frame";

		State = string.Join(Format.Separator, thread.UserState);
		HasState = State.Length > 0;
	}

	/// <summary>The runtime's thread id, which is what names a thread everywhere else here.</summary>
	public int Id { get; }

	public string IdLabel { get; }

	/// <summary>Whether this is the thread the debugger is holding the stop on.</summary>
	public bool IsStopped { get; }

	/// <summary>The innermost managed method on the thread, or a sentence saying there is none.</summary>
	public string TopFrame { get; }

	/// <summary>
	/// What the runtime says the thread is doing, as words: <c>Background  ·  WaitSleepJoin</c>.
	/// Empty when it says nothing, which a thread merely running managed code does.
	/// </summary>
	public string State { get; }

	public bool HasState { get; }
}
