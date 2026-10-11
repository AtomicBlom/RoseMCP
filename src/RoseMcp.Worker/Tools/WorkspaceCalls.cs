using ModelContextProtocol;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tools;

/// <summary>
/// The only way a tool reaches the workspace: every read, every mutation and the status report go
/// through here, and no tool type is given the host or the shared progress to go round it.
/// <para>
/// Each call has the same preamble, and the part of it that matters is easy to get wrong in a way
/// nothing notices. The call's wait has to follow the shared load and reload reports, through a
/// handle held across the whole wait and the work after it. A tool that forgets the handle, or
/// lets it go before the await, still answers correctly; its client just sees nothing at all
/// while it sits behind a cold load measured in minutes, which reads as a hang. Written once here,
/// it cannot be forgotten by a tool added later, which copies whichever neighbour it sees first.
/// </para>
/// <para>
/// Arguments are read and requests built before calling in, so a call that is going to be refused
/// is refused without paying for a load or queueing behind a mutation.
/// </para>
/// </summary>
public sealed class WorkspaceCalls(WorkspaceHost host, SharedWorkProgress sharedWork)
{
	/// <summary>
	/// Runs <paramref name="work"/> as a mutation: ordered behind every pending mutation and the
	/// disk barrier, against a freshly reconciled snapshot, with the solution it returns adopted.
	/// The wait gets at most half of the call's progress and the work the rest, starting from
	/// wherever the wait reached.
	/// </summary>
	public async Task<T> MutateAsync<T>(
		IProgress<ProgressNotificationValue> progress,
		Func<WorkspaceSession, WorkspaceSnapshot, IWorkProgress, CancellationToken, Task<MutationResult<T>>> work,
		CancellationToken cancellationToken)
	{
		var (waiting, working) = WorkProgress.Split(progress);

		return await FollowingAsync(sharedWork, waiting, async () =>
		{
			var session = await host.SessionAsync();

			return await session.MutateAsync(
				(snapshot, token) => work(session, snapshot, working, token), cancellationToken);
		});
	}

	/// <summary>
	/// Runs <paramref name="work"/> against a snapshot no older than disk, for work that reports its
	/// own progress. The wait gets at most half of the call's progress and the work the rest.
	/// </summary>
	public async Task<T> ReadAsync<T>(
		IProgress<ProgressNotificationValue> progress,
		Func<WorkspaceSnapshot, IWorkProgress, Task<T>> work,
		CancellationToken cancellationToken)
	{
		var (waiting, working) = WorkProgress.Split(progress);

		return await FollowingAsync(sharedWork, waiting, async () =>
		{
			var snapshot = await host.ReadAsync(cancellationToken);

			return await work(snapshot, working);
		});
	}

	/// <summary>
	/// Runs <paramref name="work"/> against a snapshot no older than disk, for work too quick to be
	/// worth reporting. The wait is the only thing said, so it has the whole of the call's progress:
	/// on a cold start it is the difference between an answer in milliseconds and in minutes.
	/// </summary>
	public async Task<T> ReadAsync<T>(
		IProgress<ProgressNotificationValue> progress,
		Func<WorkspaceSnapshot, Task<T>> work,
		CancellationToken cancellationToken)
	{
		return await FollowingAsync(sharedWork, WorkProgress.For(progress), async () =>
		{
			var snapshot = await host.ReadAsync(cancellationToken);

			return await work(snapshot);
		});
	}

	/// <summary>
	/// What state the workspace is in. Never throws for a failed load, unlike a read, because a
	/// caller asking that deserves an answer. The first status call after a worker starts is usually
	/// the one waiting for the whole design-time build, so the load gets the first half of the
	/// progress and describing the result -- which is where generators actually run -- gets the
	/// second.
	/// </summary>
	public async Task<WorkspaceStatusReport> StatusAsync(
		IProgress<ProgressNotificationValue> progress,
		CancellationToken cancellationToken)
	{
		var (waiting, working) = WorkProgress.Split(progress);

		return await FollowingAsync(
			sharedWork, waiting, () => host.GetStatusAsync(cancellationToken, working));
	}

	/// <summary>
	/// The analyzer assemblies this worker's reads have found rebuilt since it loaded them. Not a read: it waits
	/// on nothing and runs no barrier, so it needs none of the shared work the calls above follow, and it is
	/// here only so the worker's own info can say what the reads found without being given the host.
	/// </summary>
	public IReadOnlyList<string> RebuiltAnalyzerPaths => host.RebuiltAnalyzerPaths;

	/// <summary>
	/// Runs <paramref name="call"/> with <paramref name="waiting"/> following the shared work for as
	/// long as it takes, including whatever is going on at the moment it starts, and lets go once
	/// it ends however it ends.
	/// </summary>
	public static async Task<T> FollowingAsync<T>(
		SharedWorkProgress sharedWork,
		IWorkProgress? waiting,
		Func<Task<T>> call)
	{
		using var following = sharedWork.Follow(waiting);

		return await call();
	}
}
