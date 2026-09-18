using System.Diagnostics.CodeAnalysis;

using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// The process a session is debugging and what it is doing, as one thing that can be asked.
/// <para>
/// These were separate because they arrive separately -- a process comes from an attach, a state
/// from a callback -- but nothing useful can be done with either alone. Every verb in a session
/// starts by asking the same question: is there still a target, and is it stopped. Kept apart, that
/// question gets asked in as many spellings as there are callers, and each one decides for itself
/// what the pair means.
/// </para>
/// <para>
/// So this owns both, and owns the lock that makes reading them one answer rather than two. The
/// lock serializes everything a session does to its target, which is why the collaborators that
/// hold no lock of their own -- the breakpoint table, the symbol registry, a stop record -- are
/// documented as needing this one: it names the thing they are being serialized against.
/// </para>
/// <para>
/// Every transition is here, so there is exactly one writer. A state that two types could move is
/// a state that can disagree with itself, which is what having a single value for it was for.
/// </para>
/// </summary>
internal sealed class DebuggedTarget(DebugEventBuffer buffer, ILogger logger)
{
	private readonly RuntimeAttachment _runtime = new(buffer, logger);

	private TargetExecution _execution = new TargetExecution.Running();

	/// <summary>
	/// The lock guarding this target. Taken by anything that reads the state and then acts on what
	/// it read, because between those two the target can stop, exit or be let go.
	/// </summary>
	internal Lock Gate { get; } = new();

	/// <summary>The process being debugged, or null before an attach has succeeded.</summary>
	internal CorDebugProcess? Process => _runtime.Process;

	/// <summary>The pid attached to, which outlives the process object for reporting.</summary>
	internal int? ProcessId => _runtime.ProcessId;

	/// <summary>
	/// What the target is doing. One read of one reference, so a caller cannot catch two halves of a
	/// transition -- and safe to ask for without the gate, which is what the callback thread needs
	/// while a detach holds it.
	/// </summary>
	internal TargetExecution Execution => Volatile.Read(ref _execution);

	/// <summary>Whether there is still a target to talk to: not gone, not let go, not part-way through either.</summary>
	internal bool IsLive => Execution.IsLive;

	/// <summary>Whether the target is being held at a stop.</summary>
	internal bool IsStopped => Execution is TargetExecution.Stopped;

	/// <summary>Whether a detach is stepping the target off its breakpoint patches.</summary>
	internal bool IsDetaching => Execution is TargetExecution.Detaching;

	/// <summary>Whether the debugger has let the target go.</summary>
	internal bool IsDetached => Execution is TargetExecution.Detached;

	/// <summary>Whether the target process has ended.</summary>
	internal bool HasExited => Execution is TargetExecution.Exited;

	/// <summary>The stop being held, or null in every other state.</summary>
	internal StopRecord? Stop => Execution.Stop;

	/// <summary>
	/// The live process and the stop it is being held at, or false when either is missing. The one
	/// spelling of "there is a target and it is stopped", for the verbs that need both.
	/// </summary>
	internal bool TryHeld(
		[NotNullWhen(true)] out CorDebugProcess? process,
		[NotNullWhen(true)] out StopRecord? stop)
	{
		process = Process;
		stop = Stop;
		return process is not null && stop is not null;
	}

	/// <summary>
	/// The live process, or false when there is nothing to talk to. The one spelling of "there is
	/// still a target", for the verbs that do not care whether it is stopped.
	/// </summary>
	internal bool TryLive([NotNullWhen(true)] out CorDebugProcess? process)
	{
		process = IsLive ? Process : null;
		return process is not null;
	}

	/// <summary>
	/// Holds the target at <paramref name="stop"/>. Whatever was held before is over and its timers
	/// go with it: a new stop never inherits the previous one's hold.
	/// </summary>
	internal void TakeStop(StopRecord stop)
	{
		Stop?.Dispose();
		Move(new TargetExecution.Stopped(stop));
	}

	/// <summary>
	/// Ends the stop being held, standing down its timers, and hands back what it was -- or null when
	/// nothing was stopped. The target is left running, which is what it is about to be doing in every
	/// caller: each of them is the thing that resumes it.
	/// </summary>
	internal StopRecord? EndStop()
	{
		var stop = Stop;
		stop?.Dispose();
		Move(new TargetExecution.Running());
		return stop;
	}

	/// <summary>
	/// Opens the window in which a detach steps the target off its breakpoint patches, and hands back
	/// the token that closes it. In that window the target runs with its breakpoints still live, so a
	/// callback can arrive and must be answered without the gate, which the detaching thread holds.
	/// </summary>
	internal TargetExecution OpenDetachWindow()
	{
		var window = new TargetExecution.Detaching();
		Move(window);
		return window;
	}

	/// <summary>
	/// Closes the window <paramref name="window"/> opened, leaving the target running.
	/// <para>
	/// Compared rather than assigned: the target can have exited inside the window, and a process
	/// that has gone outranks one that is running again.
	/// </para>
	/// </summary>
	internal void CloseDetachWindow(TargetExecution window) =>
		Interlocked.CompareExchange(ref _execution, new TargetExecution.Running(), window);

	/// <summary>Records that the debugger has let the target go; it keeps running.</summary>
	internal void Detached() => Move(new TargetExecution.Detached());

	/// <summary>
	/// Records that the target process has ended, ending any stop it was being held at. Its timers
	/// would otherwise outlive the process and a reader would be told a dead target is stopped at a
	/// breakpoint.
	/// </summary>
	internal void Exited()
	{
		lock (Gate)
		{
			Stop?.Dispose();
			Move(new TargetExecution.Exited());
		}
	}

	/// <summary>
	/// Records an exit seen inside a detach window, where the gate belongs to the detaching thread
	/// and there is no stop to end, since opening the window ended it.
	/// </summary>
	internal void ExitedWhileDetaching() => Move(new TargetExecution.Exited());

	internal void Attach(int pid, TimeSpan runtimeReadyTimeout, EventHandler<CorDebugManagedCallbackEventArgs> onEvent) =>
		_runtime.Attach(pid, runtimeReadyTimeout, onEvent);

	internal void Launch(string executablePath, string? arguments, EventHandler<CorDebugManagedCallbackEventArgs> onEvent) =>
		_runtime.Launch(executablePath, arguments, onEvent);

	internal void AttachUwpAtStartup(int pid, Action resume, TimeSpan startupTimeout, EventHandler<CorDebugManagedCallbackEventArgs> onEvent) =>
		_runtime.AttachUwpAtStartup(pid, resume, startupTimeout, onEvent);

	/// <summary>Terminates the debugging interface. The caller decides whether it is safe to.</summary>
	internal void Terminate() => _runtime.Terminate();

	private void Move(TargetExecution next) => Volatile.Write(ref _execution, next);

	/// <summary>
	/// The stop being held, described, or null when the target is running. The one call a reader
	/// needs before asking for frames, locals or threads at all.
	/// </summary>
	internal LiveStop? CurrentStop()
	{
		lock (Gate)
		{
			return Stop?.Describe();
		}
	}
}
