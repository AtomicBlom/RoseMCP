using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// What an event says about where the target stopped: the frames it stopped in, the values in the
/// innermost one, and the exception that put it there.
/// <para>
/// All of it is read from a thread and nothing else. A stop's account of itself is captured on the
/// callback that announced it, because that is the moment the thread is known to be stopped, and by
/// the time anybody reads the event the target may be running again.
/// </para>
/// <para>
/// Nothing here throws. An event that cannot describe where it happened is still worth more than no
/// event, so each reader degrades to a placeholder or an empty list and says so in the log rather
/// than taking the callback down with it.
/// </para>
/// </summary>
internal sealed class StopNarrative(ILogger logger)
{
	private readonly CorDebugInspector _inspector = new(logger);

	/// <summary>
	/// The deepest a stop's stack goes in an event. A stack is bounded because a runaway recursion
	/// has tens of thousands of frames and reading each one costs metadata lookups, on a callback the
	/// target is stopped for.
	/// </summary>
	private const int MaxFrames = 20;

	/// <summary>
	/// The managed frames of a stopped thread, innermost first, resolved to method names. Only valid
	/// while the thread is stopped -- which, for an exception or a stopping breakpoint, is the callback
	/// it is reported on. Frames whose function cannot be resolved (native, internal, dynamic) are
	/// skipped.
	/// </summary>
	internal IReadOnlyList<string> Frames(CorDebugThread thread)
	{
		var frames = new List<string>();
		try
		{
			foreach (var chain in thread.EnumerateChains())
			{
				foreach (var frame in chain.EnumerateFrames())
				{
					if (frames.Count >= MaxFrames) return frames;

					var described = DescribeFrame(frame);
					if (described is not null) frames.Add(described);
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Walking a thread's stack failed.");
		}

		return frames;
	}

	/// <summary>
	/// The top managed frame's arguments and locals, captured on the callback that announced a stop
	/// so the event stream carries them without a second call.
	/// <para>
	/// The same reader a frame request uses, so what a stop event says and what
	/// <c>rose_live_app_frame_variables</c> says about frame 0 cannot drift apart -- including the
	/// paths, which is what lets a caller expand a value it saw in an event.
	/// Which instance of that reader answers cannot matter: it holds no state and is handed the
	/// frame per call.
	/// </para>
	/// </summary>
	internal IReadOnlyList<LiveVariable> TopFrameVariables(CorDebugThread thread)
	{
		try
		{
			var frame = CorDebugInspector.FindTopILFrame(thread);
			if (frame is null) return [];

			return _inspector.VariablesOf(frame).Variables;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading the stopped frame's variables failed.");
			return [];
		}
	}

	/// <summary>
	/// The type of the exception a thread is reporting, named from its class's metadata. A first-chance
	/// exception's type is the whole reason a reader looks at the event, so it degrades to a phrase
	/// saying which part could not be read rather than to nothing.
	/// </summary>
	internal static string ExceptionType(CorDebugThread thread)
	{
		try
		{
			var value = thread.CurrentException;
			if (value is CorDebugReferenceValue reference)
			{
				value = reference.Dereference();
			}

			if (value is CorDebugObjectValue obj)
			{
				var cls = obj.Class;
				return MethodTokens.TypeName(cls.Module.Name, cls.Token) ?? $"type token 0x{(int)cls.Token:x8}";
			}

			return value?.GetType().Name ?? "(no exception object)";
		}
		catch (Exception)
		{
			return "(unresolved exception type)";
		}
	}

	private static string? DescribeFrame(CorDebugFrame frame)
	{
		try
		{
			var function = frame.Function;
			return MethodTokens.MethodFullName(function.Module.Name, (int)function.Token);
		}
		catch (Exception)
		{
			return null; // Native, internal, or otherwise unresolvable frame.
		}
	}
}
