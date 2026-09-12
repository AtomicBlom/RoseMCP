namespace RoseMcp.Contracts;

/// <summary>
/// Every managed thread of a stopped target, held thread first.
/// <para>
/// Only while stopped, and that is a limit of the mechanism rather than a choice: enumerating
/// threads and walking their stacks requires the runtime to be synchronized, and synchronizing it
/// to answer a question nobody asked would stop somebody's application.
/// </para>
/// </summary>
public sealed record LiveThreadList : LiveExecutionReport
{
	public IReadOnlyList<LiveThread> Threads { get; init; } = [];
}
