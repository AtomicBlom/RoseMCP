using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Reading a target that is held: its stack, a frame's arguments and locals, what is inside a
/// value, its threads, and the result of an expression.
/// <para>
/// Apart from the session that holds the stop, because holding one and reading one are different
/// jobs. Every answer here is a function of two things -- the stop the target is at, and a
/// <see cref="CorDebugInspector"/> that is handed the process and the thread per call and keeps
/// nothing between them. None of it can make the target move.
/// </para>
/// <para>
/// Nothing held is reported as a running target rather than refused, on every one of these. A stop
/// ends for reasons the caller did not cause -- the safety timer is the usual one -- so a reader
/// polling a stop it had a moment ago is not making a broken call, and an error would read as one.
/// </para>
/// </summary>
internal sealed class TargetInspection(DebuggedTarget target, ILogger logger)
{
	private readonly CorDebugInspector _inspector = new(logger);

	/// <summary>How many frames a caller gets when it does not say. Enough to see how it got here.</summary>
	private const int DefaultFrameLimit = 50;

	/// <summary>
	/// What every inspection answers with when the target is not held. Said rather than refused: a
	/// stop ends for reasons the caller did not cause, and an error would read as a broken call.
	/// </summary>
	private const string NotStoppedDetail =
		"The target is running. Frames, variables and threads can only be read while it is held at a "
			+ "breakpoint or a step.";

	/// <summary>
	/// A page of a stopped thread's call stack. Reports the target as running rather than refusing
	/// when nothing is held, because a stop ends on its own and a caller polling one is not at fault.
	/// </summary>
	/// <param name="threadId">The thread to walk, or null for the one the debugger is holding.</param>
	/// <param name="offset">Where to start, zero being the innermost frame.</param>
	/// <param name="limit">How many frames to report, or null for a sensible page.</param>
	/// <exception cref="ArgumentException">The offset or limit is negative, or the thread is not there.</exception>
	internal LiveStackFrames ReadFrames(int? threadId, int offset, int? limit)
	{
		if (offset < 0) throw new ArgumentException($"A frame offset cannot be negative; {offset} was asked for.");
		if (limit is < 1) throw new ArgumentException($"A frame limit has to be at least 1; {limit} was asked for.");

		lock (target.Gate)
		{
			if (target.CurrentStop() is not { } stop)
			{
				return new LiveStackFrames
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					ThreadId = threadId,
					Offset = offset,
					Total = 0,
					Truncated = false,
				};
			}

			return _inspector.Frames(Stopped(stop), threadId, offset, limit ?? DefaultFrameLimit);
		}
	}

	/// <summary>
	/// One frame's arguments and locals. The frame is named by its index in the stack this session
	/// would report now, so a caller reads a stack and then asks about a row of it.
	/// </summary>
	/// <exception cref="ArgumentException">The index is negative, the thread is not there, or there is no such frame.</exception>
	internal LiveFrameVariables ReadFrameVariables(int frameIndex, int? threadId)
	{
		if (frameIndex < 0) throw new ArgumentException($"A frame index cannot be negative; {frameIndex} was asked for.");

		lock (target.Gate)
		{
			if (target.CurrentStop() is not { } stop)
			{
				return new LiveFrameVariables
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					FrameIndex = frameIndex,
					ThreadId = threadId,
					Symbols = LiveSymbolState.NoSymbols,
					Truncated = false,
				};
			}

			return _inspector.Variables(Stopped(stop), frameIndex, threadId);
		}
	}

	/// <summary>
	/// What is inside a value: an object's fields, or an array's elements, addressed by the same
	/// <see cref="LiveVariable.Path"/> the value was reported under.
	/// </summary>
	/// <exception cref="ArgumentException">The path does not parse, the frame is not there, or the path resolves to nothing.</exception>
	internal LiveValueExpansion Expand(string path, int frameIndex, int? threadId)
	{
		// Parsed before the lock, because a path that does not parse is the caller's mistake and
		// there is no reason to hold the session to say so.
		var parsed = ValuePath.Parse(path);
		if (frameIndex < 0) throw new ArgumentException($"A frame index cannot be negative; {frameIndex} was asked for.");

		lock (target.Gate)
		{
			if (target.CurrentStop() is not { } stop)
			{
				return new LiveValueExpansion
				{
					Execution = LiveExecutionState.Running,
					Detail = NotStoppedDetail,
					Path = path,
					Total = 0,
					Truncated = false,
				};
			}

			return _inspector.Expand(Stopped(stop), parsed, path, frameIndex, threadId);
		}
	}

	/// <summary>
	/// Every managed thread of the stopped target, the held one first. Only while stopped: reading
	/// threads needs the runtime synchronized, and synchronizing it to answer would stop the app.
	/// </summary>
	internal LiveThreadList ReadThreads()
	{
		lock (target.Gate)
		{
			if (target.CurrentStop() is not { } stop)
			{
				return new LiveThreadList { Execution = LiveExecutionState.Running, Detail = NotStoppedDetail };
			}

			return _inspector.Threads(Stopped(stop));
		}
	}

	/// <summary>
	/// Evaluates a field-access expression against the held frame. No debuggee code runs, so a
	/// property with a getter cannot be read and nothing the expression names can have a side effect.
	/// </summary>
	internal LiveEvaluation Evaluate(string expression)
	{
		lock (target.Gate)
		{
			if (!target.TryHeld(out _, out var stop))
			{
				return new LiveEvaluation { Expression = expression, Error = "The target is not stopped; evaluation needs a stop at a breakpoint or step." };
			}

			return _inspector.Evaluate(stop.Thread, expression);
		}
	}

	/// <summary>
	/// The stopped state, for handing to the inspector. Only correct while the target's gate is held and
	/// the target is stopped, which is why every caller above is inside the lock and past the guard.
	/// </summary>
	private StoppedTarget Stopped(LiveStop stop) => new(target.Process!, target.Stop?.Thread, stop);

	/// <summary>
	/// Whether a thread has any managed frames on it, which is what makes it worth pausing on: a
	/// thread with none gives a stop with nothing to read.
	/// </summary>
	internal bool HasManagedFrames(CorDebugThread thread) => _inspector.WalkFrames(thread).Frames.Count > 0;
}
