using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;

namespace RoseMcp.LiveApp;

/// <summary>
/// What the inspector asks of one debug session, answered against the target it has or refused in
/// the shape of the answer.
/// <para>
/// These belong together because of how they fail, not because of what they read. Every call here
/// reports "no target" as an ordinary result; the calls that throw instead -- continuing, stepping,
/// moving a breakpoint -- are ones a person asked for once, where an exception is the answer. The
/// inspector polls. It asks for frames, threads and variables on a timer against a session it may
/// be about to lose, and a caller that has to catch an exception on the turn a target goes away
/// reads a different shape of answer at that moment than at every other one.
/// </para>
/// <para>
/// A view over the session rather than something with a life of its own. The host resolves the
/// session under its own lock and hands it here for the length of one question, so nothing here
/// can go on describing a target after it has gone.
/// </para>
/// </summary>
internal readonly struct InspectorSurface(CorDebugSession? session)
{
	/// <summary>
	/// What an inspection answers with when the host has no target at all. Said as a running
	/// report rather than thrown, so a caller polling a session it is about to lose reads the same
	/// shape of answer it reads at every other moment.
	/// </summary>
	private const string NotAttachedDetail = "This session is not attached to a target, so there is nothing to read.";

	/// <summary>A page of a stopped thread's call stack, with file and line where symbols allow.</summary>
	public LiveStackFrames ReadFrames(int? threadId, int offset, int? limit)
	{
		if (session is null)
		{
			return new LiveStackFrames
			{
				Execution = LiveExecutionState.Running,
				Detail = NotAttachedDetail,
				ThreadId = threadId,
				Offset = offset,
				Total = 0,
				Truncated = false,
			};
		}

		return session.Inspection.ReadFrames(threadId, offset, limit);
	}

	/// <summary>One frame's arguments and locals, named from the module's symbols where there are any.</summary>
	public LiveFrameVariables ReadFrameVariables(int frameIndex, int? threadId)
	{
		if (session is null)
		{
			return new LiveFrameVariables
			{
				Execution = LiveExecutionState.Running,
				Detail = NotAttachedDetail,
				FrameIndex = frameIndex,
				ThreadId = threadId,
				Symbols = LiveSymbolState.NoSymbols,
				Truncated = false,
			};
		}

		return session.Inspection.ReadFrameVariables(frameIndex, threadId);
	}

	/// <summary>What is inside a value: an object's fields, or an array's elements.</summary>
	public LiveValueExpansion ExpandValue(string path, int frameIndex, int? threadId)
	{
		if (session is null)
		{
			return new LiveValueExpansion
			{
				Execution = LiveExecutionState.Running,
				Detail = NotAttachedDetail,
				Path = path,
				Total = 0,
				Truncated = false,
			};
		}

		return session.Inspection.Expand(path, frameIndex, threadId);
	}

	/// <summary>Every managed thread of the stopped target, the held one first.</summary>
	public LiveThreadList ReadThreads()
	{
		if (session is null)
		{
			return new LiveThreadList { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail };
		}

		return session.Inspection.ReadThreads();
	}

	/// <summary>Takes or releases an operator's hold, which suspends the stop's safety timer.</summary>
	public LiveHoldResult Hold(int? seconds, bool release)
	{
		if (session is null)
		{
			return new LiveHoldResult { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail, Applied = false };
		}

		return session.OperatorHold(seconds is { } requested ? TimeSpan.FromSeconds(requested) : null, release);
	}

	/// <summary>Stops a running target where it stands, rather than where a breakpoint would.</summary>
	public LivePauseResult Break(int? autoContinueSeconds)
	{
		if (session is null)
		{
			return new LivePauseResult { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail, Paused = false };
		}

		return session.Break(autoContinueSeconds);
	}

	/// <summary>Methods of the target's loaded modules matching a typed query, best first.</summary>
	public LiveMethodMatches SearchMethods(string? query, int limit)
	{
		if (session is null)
		{
			return new LiveMethodMatches
			{
				Query = query ?? string.Empty,
				Matches = [],
				Total = 0,
				ModulesSearched = 0,
				Detail = NotAttachedDetail,
			};
		}

		return session.Bindings.SearchMethods(query, limit);
	}

	/// <summary>A method's source and the positions inside it a breakpoint can be set at.</summary>
	public LiveMethodSource ReadMethodSource(string location)
	{
		if (session is null)
		{
			return new LiveMethodSource
			{
				Location = location,
				DisplayName = location,
				Module = string.Empty,
				Symbols = LiveSymbolState.NoSymbols,
				FirstLine = 0,
				Lines = [],
				Positions = [],
				Detail = NotAttachedDetail,
			};
		}

		return session.Bindings.ReadMethodSource(location);
	}
}
