using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;
using RoseMcp.LiveApp.Xaml;
using RoseMcp.Logging;

namespace RoseMcp.LiveApp;

/// <summary>
/// Owns the one target this host was launched for, and everything the broker can ask about it: a real
/// ICorDebug session capturing debug events into a buffer the broker reads, the XAML facade over the
/// running visual tree, and the paths that launch a target rather than attach to one -- an ordinary
/// executable, and a packaged UWP app activated by AUMID so the debugger is there from its first
/// module load.
/// <para>
/// One target and not several, because ICorDebug, the XAML diagnostics tap and the architecture the
/// host must match are all per process. The broker owns the fan-out.
/// </para>
/// </summary>
public sealed class LiveAppSessionHost(LiveAppOptions options, ILogger<LiveAppSessionHost> logger) : IHostedService
{
	private static readonly TimeSpan UwpRuntimeReadyTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan UwpStartupTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// What an inspection answers with when this host has no target at all. Said as a running
	/// report rather than thrown, so a caller polling a session it is about to lose reads the same
	/// shape of answer it reads at every other moment.
	/// </summary>
	private const string NotAttachedDetail = "This session is not attached to a target, so there is nothing to read.";

	private readonly Lock _gate = new();
	private readonly DebugEventBuffer _events = new();
	private LiveAppSessionState _state = LiveAppSessionState.Starting;
	private int? _targetProcessId;
	private string? _detail;
	private CorDebugSession? _session;
	private string? _uwpPackageFullName;

	// Descriptive rather than a flag, so unlike _uwpPackageFullName it is not cleared when debug
	// mode is lifted: it says which build this session ran, and that stays true afterwards.
	private string? _uwpInstallLocation;
	private XamlDiagnosticsSession? _xaml;

	// Which XAML framework the target is running, once anything has established it. Kept because a
	// process cannot change the framework it has loaded, so a known answer never needs asking again --
	// and because the answer is what decides whether a XAML surface can be offered at all, which is a
	// question a status view asks long before anything asks for a tree.
	private XamlStackDetection? _xamlStack;

	// Whether somebody has asked for the target to be left running. A detach is that request, and it
	// is what separates an ordinary close -- where the app is meant to outlive the session -- from a
	// client that went away, where a launched target has nobody left to end it.
	private bool _targetReleased;

	/// <summary>The architecture this host launched as, which is the target's architecture.</summary>
	public static TargetArchitecture Architecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		_ => TargetArchitecture.Unknown,
	};

	/// <summary>
	/// What this host can say about itself and its target without doing any real work. The broker asks
	/// on connect and then polls it, so it is also what a status view reads.
	/// <para>
	/// The XAML stack is resolved here rather than when the session was established, because a
	/// framework loads late: a target attached at startup has not loaded its XAML DLL yet, and one
	/// early look answered <see cref="XamlStack.Unknown"/> forever would be a wrong answer rather than
	/// an unknown one. Re-probing while it is unknown costs a module-list read.
	/// </para>
	/// </summary>
	public LiveAppInfo CurrentInfo()
	{
		int? targetProcessId;
		XamlStackDetection? known;
		XamlDiagnosticsSession? xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			known = _xamlStack;
			xaml = _xaml;
		}

		// Outside the gate. It costs microseconds, but it is a call into another process and nothing
		// else in this host should queue behind one.
		var stack = ResolveXamlStack(targetProcessId, known, xaml);

		lock (_gate)
		{
			_xamlStack = stack;

			// Read once, because two calls could straddle a resume and describe a stop that was never
			// in the state the pair of them imply.
			var stop = _session?.CurrentStop();

			return new LiveAppInfo
			{
				HostProcessId = Environment.ProcessId,
				Architecture = Architecture,
				State = EffectiveState(),
				TargetProcessId = _targetProcessId,
				InstallLocation = _uwpInstallLocation,
				Detail = _detail,
				Execution = stop?.State ?? LiveExecutionState.Running,
				Stop = stop,
				XamlStack = stack?.Stack ?? XamlStack.Unknown,
				XamlStackReason = stack?.Reason
					?? "this session has no target process yet, so its loaded modules cannot be read",
				XamlProvider = xaml?.Provider ?? LiveXamlProvider.None,
				LastEvent = _events.Newest(),
				HostLogPath = RoseFileLogging.Destination,
			};
		}
	}

	/// <summary>
	/// Which XAML framework the target is running: what a real XAML request found, else what a previous
	/// probe found, else a fresh probe.
	/// <para>
	/// A known answer is never re-probed, because a process cannot change the framework it has loaded.
	/// An answer a request arrived at wins over this host's own probe: they read the same module list
	/// and normally agree, and where they do not, the request's is the one a tap was chosen by. With no
	/// target process there is nothing to read, and the previous answer -- usually none -- stands.
	/// </para>
	/// </summary>
	private static XamlStackDetection? ResolveXamlStack(
		int? targetProcessId,
		XamlStackDetection? known,
		XamlDiagnosticsSession? xaml)
	{
		if (xaml?.Stack is { Stack: not XamlStack.Unknown } fromRequest) return fromRequest;
		if (known is { Stack: not XamlStack.Unknown }) return known;
		if (targetProcessId is not { } pid) return known;

		return XamlStackProbe.Detect(pid);
	}

	/// <summary>
	/// Records which package this session is driving and where it is installed from, and says so.
	/// <para>
	/// The install location is the fact that makes a stale registration visible, and it is the fact
	/// that was being dropped. Two layouts under one identity and version -- a <c>Release\AppX</c>
	/// registered while a fresh <c>Debug\AppX</c> sits beside it -- is an ordinary state to reach,
	/// because <c>Add-AppxPackage -Register</c> silently does nothing when a package of the same
	/// identity is already registered. Everything after that describes the build nobody meant to run,
	/// accurately, which is the worst way to be wrong.
	/// </para>
	/// <para>
	/// Both in the event stream and on the session, deliberately. The notice is where somebody
	/// reading what happened sees it at the moment it mattered; the field is where a caller that
	/// only ever reads a result finds it.
	/// </para>
	/// </summary>
	private void NoteUwpPackage(string packageFullName)
	{
		var installLocation = Uwp.ResolveInstallLocation(packageFullName);

		lock (_gate)
		{
			// Both, and the first is what DisableUwpDebugging reads to know it has something to lift.
			_uwpPackageFullName = packageFullName;
			_uwpInstallLocation = installLocation;
		}

		_events.Append(
			LiveDebugEventKind.SessionNotice,
			installLocation is null
				? $"{packageFullName} is registered, but its install location could not be read."
				: $"{packageFullName} is registered from {installLocation}. If that is not the layout you "
					+ "just built, the registration is stale and this session is debugging the wrong build.");
	}

	/// <summary>Adds a tracepoint to the attached target.</summary>
	public LiveTracepoint AddTracepoint(string location, string? logMessage, int? logEveryNthHit, string? condition)
		=> RequireSession().AddTracepoint(location, logMessage, logEveryNthHit, condition);

	public LiveTracepointList ListTracepoints()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveTracepointList { Tracepoints = session?.ListTracepoints() ?? [] };
	}

	public LiveTracepointList RemoveTracepoint(string id)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		session?.RemoveTracepoint(id);
		return new LiveTracepointList { Tracepoints = session?.ListTracepoints() ?? [] };
	}

	/// <summary>Sets a stopping breakpoint on the attached target.</summary>
	public LiveBreakpoint SetBreakpoint(string location, int? autoContinueSeconds, string? condition)
		=> RequireSession().AddBreakpoint(location, autoContinueSeconds, condition);

	public LiveBreakpointList ListBreakpoints()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveBreakpointList { Breakpoints = session?.ListBreakpoints() ?? [] };
	}

	public LiveBreakpointList RemoveBreakpoint(string id)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		session?.RemoveBreakpoint(id);
		return new LiveBreakpointList { Breakpoints = session?.ListBreakpoints() ?? [] };
	}

	/// <summary>Resumes a target held at a stopping breakpoint; false when nothing was stopped.</summary>
	public LiveContinueResult Continue()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		// The session's own result, forwarded rather than reduced to a bool: it carries whether the
		// resume released an operator's hold, which nothing else would say.
		return session?.Continue() ?? new LiveContinueResult { Continued = false };
	}

	/// <summary>Steps a target held at a breakpoint: "in", "over", or "out".</summary>
	public LiveContinueResult Step(string mode)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return session?.Step(mode) ?? new LiveContinueResult { Continued = false };
	}

	/// <summary>Evaluates a field-access expression against the stopped frame; safe, no debuggee code runs.</summary>
	public LiveEvaluation Evaluate(string expression)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return session?.Evaluate(expression)
			?? new LiveEvaluation { Expression = expression, Error = "This session is not attached to a target." };
	}

	/// <summary>A page of a stopped thread's call stack, with file and line where symbols allow.</summary>
	public LiveStackFrames ReadFrames(int? threadId, int offset, int? limit)
	{
		if (Attached() is not { } session)
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

		return session.ReadFrames(threadId, offset, limit);
	}

	/// <summary>One frame's arguments and locals, named from the module's symbols where there are any.</summary>
	public LiveFrameVariables ReadFrameVariables(int frameIndex, int? threadId)
	{
		if (Attached() is not { } session)
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

		return session.ReadFrameVariables(frameIndex, threadId);
	}

	/// <summary>What is inside a value: an object's fields, or an array's elements.</summary>
	public LiveValueExpansion ExpandValue(string path, int frameIndex, int? threadId)
	{
		if (Attached() is not { } session)
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

		return session.Expand(path, frameIndex, threadId);
	}

	/// <summary>Every managed thread of the stopped target, the held one first.</summary>
	public LiveThreadList ReadThreads()
	{
		if (Attached() is not { } session)
		{
			return new LiveThreadList { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail };
		}

		return session.ReadThreads();
	}

	/// <summary>Takes or releases an operator's hold, which suspends the stop's safety timer.</summary>
	public LiveHoldResult Hold(int? seconds, bool release)
	{
		if (Attached() is not { } session)
		{
			return new LiveHoldResult { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail, Applied = false };
		}

		return session.Hold(seconds is { } requested ? TimeSpan.FromSeconds(requested) : null, release);
	}

	/// <summary>Stops a running target where it stands, rather than where a breakpoint would.</summary>
	public LivePauseResult Break(int? autoContinueSeconds)
	{
		if (Attached() is not { } session)
		{
			return new LivePauseResult { Execution = LiveExecutionState.Running, Detail = NotAttachedDetail, Paused = false };
		}

		return session.Break(autoContinueSeconds);
	}

	/// <summary>Methods of the target's loaded modules matching a typed query, best first.</summary>
	public LiveMethodMatches SearchMethods(string? query, int limit)
	{
		if (Attached() is not { } session)
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

		return session.SearchMethods(query, limit);
	}

	/// <summary>A method's source and the positions inside it a breakpoint can be set at.</summary>
	public LiveMethodSource ReadMethodSource(string location)
	{
		if (Attached() is not { } session)
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

		return session.ReadMethodSource(location);
	}

	/// <summary>The debug session, or null when this host has no target.</summary>
	private CorDebugSession? Attached()
	{
		lock (_gate)
		{
			return _session;
		}
	}

	/// <summary>
	/// Why a XAML request cannot be served at this instant, or null when it can be.
	/// <para>
	/// A target this session is holding is the case worth naming, and it is the one thing a debugger can
	/// know that nothing else can. <c>InitializeXamlDiagnosticsEx</c> does not return until the target's
	/// own UI thread has created and sited the tap, so a stopped target cannot serve the request at all
	/// -- and the bounds guarantee the shape of the failure rather than merely risking it: the endpoint
	/// is given twenty seconds and a held target auto-continues after thirty, so every such request
	/// expires first, and then blames the app for having no XAML UI. Visual Studio does not meet this
	/// because it is the debugger as well as the diagnostics client. So are we; the signal was simply
	/// never asked for.
	/// </para>
	/// <para>
	/// A stop an operator is holding names the hold, because the advice differs: an ordinary stop
	/// frees itself on the safety timer and waiting works, while a held one does not and waiting is
	/// the wrong thing to do.
	/// </para>
	/// </summary>
	private string? WhyXamlIsUnservable()
	{
		if (Attached()?.CurrentStop() is not { } stop) return null;

		var freed = stop.Resume == LiveStopResume.HeldByOperator
			? "An operator is holding this stop, so the auto-continue timer will not free it: release the hold "
				+ "or continue the target."
			: "Resume the target and ask again -- a held target also releases itself on the auto-continue timer.";

		return "The target is stopped, so its UI thread cannot serve a XAML request: the diagnostics "
			+ "endpoint is created by that thread and this session is holding it. " + freed;
	}

	/// <summary>
	/// A failed XAML detail with the target's own heartbeat added, which is what separates the two
	/// causes the channel's HRESULT cannot.
	/// <para>
	/// A handshake that fails means either a target that is executing and not serving diagnostics, or a
	/// target that is not executing at all, and nothing about the process tells them apart: CPU time
	/// stops climbing either way, the window stops repainting either way, and a frozen app's threads
	/// are stopped through its job object without any of them being marked suspended, so thread state
	/// cannot see it either. What a target that has stopped executing does do is stop producing debug
	/// events, so the age of the last one is the discriminator.
	/// </para>
	/// <para>
	/// Two causes reach the second state. Injection itself is one, and it is the one measured here: the
	/// call is served by the target's UI thread, and when it does not return, that thread never runs
	/// again -- an age that starts climbing from the moment of the first injection is that, exactly. A
	/// backgrounded UWP app whose package has no debug mode is the other, since PLM freezes it, and a
	/// detach lifts debug mode while deliberately leaving the app running.
	/// </para>
	/// </summary>
	private string WithTargetHeartbeat(string detail)
	{
		if (_events.Newest() is not { } newest) return detail;

		var age = DateTime.UtcNow - newest.TimestampUtc;

		// Logged as well as returned. The detail reaches whoever made the call; the log is where anyone
		// reading a run afterwards is, and a wedge is diagnosed from the log long after the result is
		// gone -- which it was, from a suite run, once this number existed to read.
		logger.LogWarning(
			"A XAML request failed and the target's last debug event ({Kind}) was {AgeSeconds:0.0}s ago.",
			newest.Kind,
			age.TotalSeconds);

		// Seconds rather than a verdict. Which ages are suspicious depends on what the target does when
		// it is idle -- a probe on a timer is silent for milliseconds, a real app for minutes -- and a
		// threshold picked here would be a guess presented as a diagnosis.
		return detail
			+ $" The target's last debug event ({newest.Kind}) was {age.TotalSeconds:0.0}s ago. If that is not "
			+ "recent the target has stopped executing rather than stopped answering: either the injection "
			+ "call never returned, which leaves the UI thread that serves it stuck, or the app is a "
			+ "backgrounded UWP one that PLM has frozen because its package no longer has debug mode.";
	}

	/// <summary>
	/// Injects the XAML diagnostics provider into the target and returns a snapshot of its live visual
	/// tree. Optionally rooted at a named element (its subtree only) and paged, since a real app's tree is
	/// large. Returns a tree carrying only a detail (no nodes) when the target has no XAML UI or the
	/// provider is unavailable, rather than throwing.
	/// </summary>
	public LiveXamlTree ReadXamlTree(string? rootName, int offset, int limit)
	{
		int? targetProcessId;
		string? installLocation;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			installLocation = _uwpInstallLocation;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlTree { Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlTree { Detail = held };

		var tree = xaml.ReadTree(pid);
		if (tree.Detail is not null) return tree with { Detail = WithTargetHeartbeat(tree.Detail) };

		IReadOnlyList<LiveXamlNode> matched = tree.Nodes;
		if (!string.IsNullOrWhiteSpace(rootName))
		{
			var root = tree.Nodes.FirstOrDefault(node => node.Name == rootName);
			if (root is null) return new LiveXamlTree { Detail = $"No element named '{rootName}' is in the tree." };
			matched = Subtree(tree.Nodes, root);
		}

		var page = matched.Skip(Math.Max(0, offset)).Take(limit > 0 ? limit : int.MaxValue).ToList();

		// Carried on every page, because this is the answer that looks right when it is not: the
		// nodes below name source files, and a stale registration makes those files the wrong ones.
		//
		// The channel is carried for the same reason, and it is easy to lose here: paging builds a new
		// result rather than narrowing the one it was given, so a field the read below filled and this
		// line does not mention is dropped silently and reads as `not reported`.
		return new LiveXamlTree
		{
			Nodes = page,
			Total = matched.Count,
			InstallLocation = installLocation,
			Channel = tree.Channel,
		};
	}

	/// <summary>An element and all its descendants, from the flat node list, by walking parent handles.</summary>
	private static List<LiveXamlNode> Subtree(IReadOnlyList<LiveXamlNode> all, LiveXamlNode root)
	{
		var childrenByParent = all.Where(node => node.Handle != root.Handle)
			.ToLookup(node => node.Parent);

		var subtree = new List<LiveXamlNode>();
		var pending = new Queue<LiveXamlNode>();
		pending.Enqueue(root);
		while (pending.Count > 0)
		{
			var node = pending.Dequeue();
			subtree.Add(node);
			foreach (var child in childrenByParent[node.Handle])
			{
				pending.Enqueue(child);
			}
		}

		return subtree;
	}

	/// <summary>
	/// Reads one element's XAML properties (by the handle a tree snapshot reported) with provenance and,
	/// when the app carries source info, source location. Set properties only by default; the framework
	/// defaults are included on request.
	/// </summary>
	public LiveXamlProperties ReadXamlProperties(ulong handle, bool includeDefaults)
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlProperties { Handle = handle, Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlProperties { Handle = handle, Detail = held };

		var properties = xaml.ReadProperties(pid, handle, includeDefaults);
		return properties.Detail is null
			? properties
			: properties with { Detail = WithTargetHeartbeat(properties.Detail) };
	}

	/// <summary>
	/// Arms one of the overlay's pointer modes -- select, so the next click picks an element, or
	/// rulers, so the picked one is measured from -- or disarms whichever is on.
	/// <para>
	/// Both positions of one switch, because the toolbar has always had both and only arming was
	/// reachable from here. Arming lays a pointer-capturing layer over the app; picking by handle does
	/// not take it away, since that never goes through the click path that ends the mode, so an agent
	/// that armed and changed its mind had left the app modal with no way back.
	/// </para>
	/// </summary>
	public LiveXamlSelection EnterXamlSelectMode(
		bool includeAllElements,
		bool justMyXaml,
		bool arm = true,
		string mode = "select")
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlSelection { Detail = held };

		var selection = arm
			? xaml.EnterSelectMode(pid, includeAllElements, justMyXaml, mode)
			: xaml.ExitSelectMode(pid);

		return selection.Detail is null ? selection : selection with { Detail = WithTargetHeartbeat(selection.Detail) };
	}

	/// <summary>Reads the element the user picked by clicking it in the running app.</summary>
	public LiveXamlSelection ReadXamlSelection()
	{
		XamlDiagnosticsSession? xaml;
		lock (_gate)
		{
			xaml = _xaml;
		}

		return xaml?.ReadSelection()
			?? new LiveXamlSelection { Detail = "Select mode has not been entered for this session." };
	}

	/// <summary>
	/// Clears the picked element and the mark drawn over the app, so nothing is selected.
	/// <para>
	/// Injects, unlike <see cref="ReadXamlSelection"/>, because the mark lives in the app's own visual
	/// tree and only the provider can take it down. Clearing the files from this side alone would
	/// leave an outline over the app pointing at a selection that no longer exists.
	/// </para>
	/// </summary>
	public LiveXamlSelection ClearXamlSelection()
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlSelection { Detail = held };

		var cleared = xaml.ClearSelection(pid);
		return cleared.Detail is null ? cleared : cleared with { Detail = WithTargetHeartbeat(cleared.Detail) };
	}

	/// <summary>
	/// Selects the element a handle names, without a click. See
	/// <see cref="XamlDiagnosticsSession.SelectByHandle"/> for why that matters.
	/// </summary>
	public LiveXamlSelection SelectXamlElement(ulong handle)
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlSelection { Detail = held };

		var selected = xaml.SelectByHandle(pid, handle);
		return selected.Detail is null ? selected : selected with { Detail = WithTargetHeartbeat(selected.Detail) };
	}

	/// <summary>
	/// Live-edits the target by diffing two XAML versions and applying the edits to its visual tree.
	/// Returns each computed edit with its outcome. Naming a file rather than passing both versions is
	/// the continuous-apply path (#12): the XAML session remembers what it has already sent.
	/// </summary>
	public LiveXamlApplyResult ApplyXaml(string? oldXaml, string? newXaml, string? filePath)
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlApplyResult { Detail = "This session has no target process to inspect." };
		}

		if (WhyXamlIsUnservable() is { } held) return new LiveXamlApplyResult { Detail = held };

		var applied = xaml.ApplyEdits(pid, oldXaml, newXaml, filePath);
		return applied.Detail is null ? applied : applied with { Detail = WithTargetHeartbeat(applied.Detail) };
	}

	/// <summary>
	/// Reads a page, first waiting up to <paramref name="waitSeconds"/> for something to be in it.
	/// <para>
	/// Zero waits not at all, which is the old behaviour and still the right one for "what has
	/// happened so far". A positive value is what a turn-based agent wants instead of a poll loop: one
	/// call that comes back when there is something to say (#8). Paired with the kind filter it is
	/// also "wait for the next stop", so that needs no mechanism of its own.
	/// </para>
	/// <para>
	/// It returns as soon as the event is buffered rather than on a tick, and that is load-bearing for
	/// a stopping breakpoint: the stop auto-continues after its safety timeout, so an agent told late
	/// has less of the window left to evaluate anything in.
	/// </para>
	/// <para>
	/// Which is also why a pushed notification is the wrong shape here, rather than merely unavailable
	/// -- the natural thing to reach for is "notify me when the breakpoint hits", and it would be worse
	/// than this. A stopping breakpoint holds the target for thirty seconds by default, and a channel
	/// event is delivered on the caller's *next turn*, so the push can easily land after the target has
	/// resumed and the frozen state it was announcing is gone. Waiting on the call returns at the
	/// instant of the stop, with the caller already mid-call and the whole window in front of it. The
	/// buffer covers the case that looks like it needs a push: an agent that has to *do* something to
	/// trigger the breakpoint acts first and waits second, and the cursor means nothing that happened
	/// in between was missed.
	/// </para>
	/// </summary>
	public async Task<LiveDebugEventPage> ReadEventsAsync(
		long after,
		IReadOnlyCollection<LiveDebugEventKind>? kinds,
		int limit,
		int waitSeconds,
		CancellationToken cancellationToken)
	{
		if (waitSeconds > 0)
		{
			var bounded = Math.Min(waitSeconds, MaxWaitSeconds);
			await _events.WaitForAsync(after, kinds, TimeSpan.FromSeconds(bounded), cancellationToken);
		}

		return ReadEvents(after, kinds, limit);
	}

	/// <summary>
	/// The longest this will hold a call open, however many seconds were asked for.
	/// <para>
	/// An unbounded wait is a call that outlives the client's own per-call timeout, and that failure is
	/// worse than a short answer: the client gives up, the wait carries on here holding a reader, and
	/// the caller sees a timeout on a call that was working. It is the same hazard #44 was corrected
	/// about, arriving from the other direction -- there a long call was being removed, here one is
	/// being introduced deliberately.
	/// </para>
	/// <para>
	/// Truncating is safe in a way that truncating a read would not be, and that is what makes a cap
	/// the right answer rather than an error. Events are buffered and addressed by cursor, so a wait
	/// that comes back empty has lost nothing: the same call with the same cursor picks up whatever
	/// arrives next. A minute also sits well clear of the thirty seconds a stopping breakpoint holds
	/// the target for, so the case this exists for cannot be cut short by it.
	/// </para>
	/// </summary>
	private const int MaxWaitSeconds = 60;

	/// <summary>
	/// A page of buffered debug events after the given cursor, with the session's state. <paramref
	/// name="kinds"/> narrows it to the kinds asked for, and <paramref name="limit"/> caps the window.
	/// </summary>
	public LiveDebugEventPage ReadEvents(long after, IReadOnlyCollection<LiveDebugEventKind>? kinds = null, int limit = 500)
	{
		var (events, nextCursor, oldest, total, skipped) = _events.ReadAfter(after, limit, kinds);

		lock (_gate)
		{
			return new LiveDebugEventPage
			{
				State = EffectiveState(),
				NextCursor = nextCursor,
				OldestAvailable = oldest,
				TotalObserved = total,
				TargetProcessId = _targetProcessId,
				Events = events,
				Skipped = skipped,
			};
		}
	}

	/// <summary>
	/// Detaches the debugger while the host is still alive, leaving the target running. The broker
	/// calls this before it closes the host's stdin: an ICorDebug debuggee whose debugger simply dies
	/// is taken down by the operating system, so the detach must complete first.
	/// <para>
	/// A detach that does not succeed leaves the session <see cref="LiveAppSessionState.Faulted"/>
	/// rather than <see cref="LiveAppSessionState.Ended"/>, because the single thing Ended promises
	/// here -- the debugger is off the target -- is exactly what did not happen.
	/// </para>
	/// </summary>
	public LiveAppInfo DetachTarget()
	{
		CorDebugSession? session;
		XamlDiagnosticsSession? xaml;
		int? targetProcessId;
		lock (_gate)
		{
			session = _session;

			// Captured under the gate and read from the local afterwards, because StopAsync nulls the
			// field: reading it again below would be reading a field another thread is allowed to
			// clear between the two reads.
			xaml = _xaml;
			targetProcessId = _targetProcessId;

			// Recorded before the attempt rather than after it. Asking to detach is the request to
			// leave the target running, and it stays that request whether or not the debugger comes
			// off cleanly -- so a failed detach must not turn into this host ending the app on its
			// way out.
			_targetReleased = true;
		}

		// The tap holds an IXamlDiagnostics and an IVisualTreeService, and a detach is where a
		// session ends while there is still a channel to say so on. Before the debugger comes off,
		// because a detach that fails leaves this host alive and the app inspectable, and the
		// provider giving its interfaces back does not depend on either.
		xaml?.EndProviderSession();

		var detached = session?.Detach() ?? true;

		// For a UWP target, also lift the package's debug mode so it returns to its normal lifecycle.
		DisableUwpDebugging();

		if (!detached)
		{
			Fault($"Could not detach the debugger from pid {targetProcessId}. It is still attached, and the "
				+ "debugging interface has been left open rather than terminated, because terminating it while "
				+ "attached kills the target. rose_debug_events has the reason it failed.");

			return CurrentInfo();
		}

		lock (_gate)
		{
			if (_state == LiveAppSessionState.Ready) _state = LiveAppSessionState.Ended;
		}

		return CurrentInfo();
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Establish();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		CorDebugSession? session;
		XamlDiagnosticsSession? xaml;
		lock (_gate)
		{
			session = _session;
			_session = null;
			xaml = _xaml;
			_xaml = null;
			_state = LiveAppSessionState.Ended;
		}

		session?.Dispose();

		// The XAML sandbox folder belongs to the host that made it, so it goes when the host does.
		// Whatever the target app still holds open cannot go now, because detaching leaves that app
		// running on purpose; the next host to start sweeps the remainder once this pid is gone. A
		// folder that outlives its host accumulates a copy of the provider and a grant to ALL
		// APPLICATION PACKAGES.
		xaml?.Dispose();

		DisableUwpDebugging();
		EndLaunchedTarget();

		return Task.CompletedTask;
	}

	/// <summary>
	/// Ends a target this host started, unless somebody asked for it to be left running.
	/// <para>
	/// A host dies when its client closes its stdin, the way a worker does -- and a target it
	/// launched has nobody else to end it. Killing the test host that had wedged left both hosts and
	/// both probe apps running, and the probe app is single-instance, so the next run's fixture found
	/// an app it had not launched and could not activate its own. An orphan of this kind is not a
	/// leaked process that costs memory; it is a process the next run mistakes for its own.
	/// </para>
	/// <para>
	/// Launched only, never attached: this host did not start somebody else's app and has no business
	/// ending it. And never after a detach has been asked for, because that is the request to leave
	/// the target running -- asked for rather than succeeded, since a caller who asked has said what
	/// they want whether or not the debugger came off cleanly.
	/// </para>
	/// </summary>
	private void EndLaunchedTarget()
	{
		var launched = options.Target.Kind is LiveAppTargetKind.LaunchExecutable or LiveAppTargetKind.LaunchUwp;

		int? targetProcessId;
		bool released;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			released = _targetReleased;
		}

		if (!launched || released || targetProcessId is not { } pid) return;

		try
		{
			using var process = Process.GetProcessById(pid);
			if (process.HasExited) return;

			// The whole tree: a launched target is free to have started children, and leaving those
			// behind reaches the same end as leaving the target behind.
			process.Kill(entireProcessTree: true);
			logger.LogInformation("Ended pid {Pid}, which this host launched and nobody detached from.", pid);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
		{
			// Already gone, or gone between the look and the kill. Both mean the job is done.
			logger.LogDebug(exception, "Could not end the launched target pid {Pid}.", pid);
		}
	}

	private void Establish()
	{
		switch (options.Target.Kind)
		{
			case LiveAppTargetKind.AttachProcess:
				EstablishAttach(options.Target.ProcessId);
				break;

			case LiveAppTargetKind.LaunchExecutable:
				EstablishLaunch(options.Target.ExecutablePath, options.Target.Arguments);
				break;

			case LiveAppTargetKind.LaunchUwp:
				EstablishUwp(options.Target.AppUserModelId);
				break;

			default:
				Fault($"{options.Target.Kind} is not implemented in this build.");
				break;
		}

		AttachUiTooling();
	}

	/// <summary>
	/// Loads the XAML provider in the background, so the in-app toolbar is there for a person to use
	/// without an agent having asked a XAML question first.
	/// </summary>
	/// <remarks>
	/// Best effort in every direction: it never blocks the attach, never fails it, and gives up quietly
	/// on a target with no XAML in it, which is most of them -- a debugger attaches to anything. The
	/// stack is read from the target's own loaded modules before anything is injected, so a console app
	/// costs a module list and nothing more.
	/// <para>
	/// A launched app has usually not built a tree yet, and the diagnostics endpoint does not exist
	/// until it has, so this is expected to fail sometimes and says nothing when it does. Giving up is
	/// only cheap because the stack detection re-probes while it is Unknown: this runs the moment a
	/// session goes Ready, so on a launched app it asks before the framework has loaded, and a cached
	/// negative from that one look would answer every XAML call the session went on to serve.
	/// </para>
	/// </remarks>
	private void AttachUiTooling()
	{
		int? targetProcessId;
		XamlDiagnosticsSession xaml;
		lock (_gate)
		{
			if (_state != LiveAppSessionState.Ready) return;

			targetProcessId = _targetProcessId;
			xaml = _xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid) return;
		_ = Task.Run(() =>
		{
			try
			{
				var unready = xaml.AttachTooling(pid);
				if (unready is null)
				{
					logger.LogInformation("XAML tooling attached to pid {Pid}; the in-app toolbar is up.", pid);
					return;
				}

				logger.LogDebug("XAML tooling was not attached to pid {Pid}: {Detail}", pid, unready);
			}
			catch (Exception exception)
			{
				// Swallowed rather than faulted. This session is a debugger session that happens to be
				// able to inspect XAML, and a target with none is not a broken session.
				logger.LogDebug(exception, "Attaching XAML tooling to pid {Pid} failed.", pid);
			}
		});
	}

	private void EstablishAttach(int? processId)
	{
		if (processId is not { } pid)
		{
			Fault("An attach target needs a process id.");
			return;
		}

		try
		{
			using (var process = Process.GetProcessById(pid))
			{
				if (process.HasExited)
				{
					Fault($"Process {pid} has already exited.");
					return;
				}
			}
		}
		catch (ArgumentException)
		{
			Fault($"No process with id {pid} is running.");
			return;
		}

		var session = new CorDebugSession(_events, logger);
		try
		{
			session.Attach(pid);
		}
		catch (Exception exception)
		{
			session.Dispose();
			_events.Append(LiveDebugEventKind.SessionNotice, $"Attach failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = pid;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session established against pid {Pid} as {Architecture}.", pid, Architecture);
	}

	private void EstablishLaunch(string? executablePath, string? arguments)
	{
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			Fault("A launch target needs an executable path.");
			return;
		}

		if (!File.Exists(executablePath))
		{
			Fault($"No executable at {executablePath}.");
			return;
		}

		var session = new CorDebugSession(_events, logger);
		try
		{
			session.Launch(executablePath, arguments);
		}
		catch (Exception exception)
		{
			session.Dispose();
			_events.Append(LiveDebugEventKind.SessionNotice, $"Launch failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = session.TargetProcessId;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session launched {Path} as pid {Pid} ({Architecture}).", executablePath, session.TargetProcessId, Architecture);
	}

	/// <summary>
	/// Activates a packaged (UWP) app under the debugger. By default this is from birth (issue #5): the
	/// system creates the app suspended and launches a resume stub, so the debugger attaches before the
	/// runtime's first instruction and the whole of startup -- the first OnLaunched, its module loads,
	/// any exception it throws -- is captured. If the resume stub cannot be registered it falls back to
	/// attaching a beat after activation, which misses only that earliest window.
	/// </summary>
	private void EstablishUwp(string? appUserModelId)
	{
		if (string.IsNullOrWhiteSpace(appUserModelId))
		{
			Fault("A UWP target needs an app user-model id.");
			return;
		}

		var family = appUserModelId.Split('!', 2)[0];
		string packageFullName;
		try
		{
			packageFullName = Uwp.ResolvePackageFullName(family);
		}
		catch (Exception exception)
		{
			Fault(exception.Message);
			return;
		}

		// Checked before activating, not after: activating an app that is already running foregrounds
		// its window, so a failure here would have changed the user's screen for nothing and then
		// blamed the debugger. Refusing with the pid is the useful answer -- attaching instead would
		// silently give a mid-life session where a from-birth one was asked for, which is the whole
		// point of launching.
		var alreadyRunning = Uwp.FindRunningProcesses(family);
		if (alreadyRunning.Count > 0)
		{
			Fault(
				$"{family} is already running (pid {string.Join(", ", alreadyRunning)}), so activating it would "
					+ "only foreground the existing window and there would be no startup to catch. Attach to it "
					+ "with rose_debug_attach, or close it first to debug from birth.");
			return;
		}

		var session = new CorDebugSession(_events, logger);
		int pid;
		try
		{
			pid = ActivateUwpUnderDebugger(session, appUserModelId, packageFullName);
		}
		catch (Exception exception)
		{
			session.Dispose();
			DisableUwpDebugging();
			_events.Append(LiveDebugEventKind.SessionNotice, $"UWP launch failed: {exception.Message}");
			Fault(exception.Message);
			return;
		}

		lock (_gate)
		{
			_session = session;
			_targetProcessId = pid;
			_state = LiveAppSessionState.Ready;
			_detail = null;
		}

		logger.LogInformation("Live-app session activated {Aumid} as pid {Pid} ({Architecture}).", appUserModelId, pid, Architecture);
	}

	/// <summary>
	/// Activates the app from birth: registers a resume stub as the package's debugger, so the system
	/// creates the app suspended and hands the stub its ids. Activation itself blocks until the app is
	/// resumed, so it runs on a background thread while the stub reports the ids, the session arms its
	/// runtime-startup notification, and only then the stub resumes the app -- the ordering that catches
	/// the runtime's first breath. Falls back to a post-startup attach when the stub command line is too
	/// long to register.
	/// </summary>
	private int ActivateUwpUnderDebugger(CorDebugSession session, string appUserModelId, string packageFullName)
	{
		using var coordinator = new UwpStartupCoordinator();
		var stubCommandLine = coordinator.TryBuildStubCommandLine();
		if (stubCommandLine is null)
		{
			_events.Append(LiveDebugEventKind.SessionNotice, "The resume-stub command line is too long to register; attaching after startup instead.");
			return ActivateUwpPostStartup(session, appUserModelId, packageFullName);
		}

		Uwp.EnableDebugging(packageFullName, stubCommandLine);
		NoteUwpPackage(packageFullName);

		_events.Append(LiveDebugEventKind.SessionNotice, $"Enabled debug mode with a startup resume stub on {packageFullName}.");

		coordinator.BeginActivation(appUserModelId);
		var (pid, tid) = coordinator.WaitForStub(UwpStartupTimeout);
		_events.Append(LiveDebugEventKind.SessionNotice, $"Resume stub reported pid {pid} (thread {tid}); attaching from birth.");

		session.AttachUwpAtStartup(pid, coordinator.Resume, UwpStartupTimeout);
		coordinator.CompleteActivation(UwpStartupTimeout);

		_events.Append(LiveDebugEventKind.SessionNotice, $"Activated {appUserModelId} from birth as pid {pid}.");
		return pid;
	}

	/// <summary>
	/// The pre-#5 fallback: put the package into debug mode, activate it, and attach a beat later, by
	/// which time the very first OnLaunched has already run.
	/// </summary>
	private int ActivateUwpPostStartup(CorDebugSession session, string appUserModelId, string packageFullName)
	{
		Uwp.EnableDebugging(packageFullName);
		NoteUwpPackage(packageFullName);

		_events.Append(LiveDebugEventKind.SessionNotice, $"Enabled debug mode on {packageFullName}.");

		var pid = Uwp.ActivateApplication(appUserModelId);
		_events.Append(LiveDebugEventKind.SessionNotice, $"Activated {appUserModelId} as pid {pid}.");

		session.Attach(pid, UwpRuntimeReadyTimeout);
		return pid;
	}

	/// <summary>Turns off a package's debug mode, if one was enabled, restoring its normal lifecycle.</summary>
	private void DisableUwpDebugging()
	{
		string? packageFullName;
		lock (_gate)
		{
			packageFullName = _uwpPackageFullName;
			_uwpPackageFullName = null;
		}

		if (packageFullName is null) return;

		try
		{
			Uwp.DisableDebugging(packageFullName);
			logger.LogInformation("Disabled debug mode on {Package}.", packageFullName);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Disabling debug mode on {Package} failed.", packageFullName);
		}
	}

	private LiveAppSessionState EffectiveState()
	{
		if (_state == LiveAppSessionState.Ready && _session?.HasExited == true) return LiveAppSessionState.Ended;
		return _state;
	}

	private CorDebugSession RequireSession()
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return session ?? throw new InvalidOperationException("This session is not attached to a target.");
	}

	private void Fault(string detail)
	{
		lock (_gate)
		{
			_state = LiveAppSessionState.Faulted;
			_detail = detail;
		}

		logger.LogWarning("Live-app session faulted: {Detail}", detail);
	}
}
