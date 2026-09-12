namespace RoseMcp.Contracts;

/// <summary>
/// One managed thread of a stopped target: what the runtime calls it, what it was doing, and the
/// method on top of its stack.
/// </summary>
public sealed record LiveThread
{
	/// <summary>The runtime's thread id, which is what names a thread everywhere else here.</summary>
	public required int Id { get; init; }

	/// <summary>
	/// What the runtime says about the thread, as words: <c>Background</c>, <c>WaitSleepJoin</c>,
	/// <c>ThreadPool</c> and so on.
	/// <para>
	/// The <c>USER_</c> prefix every one of them carries in the runtime's own enum is dropped: it
	/// says nothing a reader of a thread list needs and makes a row of four flags unreadable.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> UserState { get; init; } = [];

	/// <summary>Whether this is the thread the debugger is holding the stop on.</summary>
	public required bool IsStopped { get; init; }

	/// <summary>
	/// The innermost managed method on the thread, which is what makes a list of numbered threads
	/// worth reading. Null when the thread has no managed frame.
	/// </summary>
	public string? TopFrame { get; init; }
}
