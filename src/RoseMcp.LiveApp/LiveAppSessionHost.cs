using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;
using RoseMcp.LiveApp.Xaml;

namespace RoseMcp.LiveApp;

/// <summary>
/// Owns the one target this host was launched for. For an attach it establishes a real ICorDebug
/// session over the target (issue #4, attach path) and captures its debug events into a buffer the
/// broker reads (issue #8). The XAML provider and the launch paths come in later issues; this is the
/// shell the broker supervises and everything else plugs into.
/// </summary>
public sealed class LiveAppSessionHost(LiveAppOptions options, ILogger<LiveAppSessionHost> logger) : IHostedService
{
	private static readonly TimeSpan UwpRuntimeReadyTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan UwpStartupTimeout = TimeSpan.FromSeconds(30);

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

	/// <summary>The architecture this host launched as, which is the target's architecture.</summary>
	public static TargetArchitecture Architecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		_ => TargetArchitecture.Unknown,
	};

	public LiveAppInfo CurrentInfo()
	{
		lock (_gate)
		{
			return new LiveAppInfo
			{
				HostProcessId = Environment.ProcessId,
				Architecture = Architecture,
				State = EffectiveState(),
				TargetProcessId = _targetProcessId,
				InstallLocation = _uwpInstallLocation,
				Detail = _detail,
			};
		}
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

		return new LiveContinueResult { Continued = session?.Continue() == true };
	}

	/// <summary>Steps a target held at a breakpoint: "in", "over", or "out".</summary>
	public LiveContinueResult Step(string mode)
	{
		CorDebugSession? session;
		lock (_gate)
		{
			session = _session;
		}

		return new LiveContinueResult { Continued = session?.Step(mode) == true };
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
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			installLocation = _uwpInstallLocation;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlTree { Detail = "This session has no target process to inspect." };
		}

		var tree = _xaml.ReadTree(pid);
		if (tree.Detail is not null) return tree;

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
		return new LiveXamlTree { Nodes = page, Total = matched.Count, InstallLocation = installLocation };
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
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlProperties { Handle = handle, Detail = "This session has no target process to inspect." };
		}

		return _xaml.ReadProperties(pid, handle, includeDefaults);
	}

	/// <summary>
	/// Arms interactive select mode so the next click in the app picks that element, or disarms it.
	/// <para>
	/// Both positions of one switch, because the toolbar has always had both and only arming was
	/// reachable from here. Arming lays a pointer-capturing layer over the app; picking by handle does
	/// not take it away, since that never goes through the click path that ends the mode, so an agent
	/// that armed and changed its mind had left the app modal with no way back.
	/// </para>
	/// </summary>
	public LiveXamlSelection EnterXamlSelectMode(bool includeAllElements, bool justMyXaml, bool arm = true)
	{
		int? targetProcessId;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		return arm
			? _xaml.EnterSelectMode(pid, includeAllElements, justMyXaml)
			: _xaml.ExitSelectMode(pid);
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
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		return _xaml.ClearSelection(pid);
	}

	/// <summary>
	/// Selects the element a handle names, without a click. See
	/// <see cref="XamlDiagnosticsSession.SelectByHandle"/> for why that matters.
	/// </summary>
	public LiveXamlSelection SelectXamlElement(ulong handle)
	{
		int? targetProcessId;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		return _xaml.SelectByHandle(pid, handle);
	}

	/// <summary>
	/// Live-edits the target by diffing two XAML versions and applying the edits to its visual tree.
	/// Returns each computed edit with its outcome. Naming a file rather than passing both versions is
	/// the continuous-apply path (#12): the XAML session remembers what it has already sent.
	/// </summary>
	public LiveXamlApplyResult ApplyXaml(string? oldXaml, string? newXaml, string? filePath)
	{
		int? targetProcessId;
		lock (_gate)
		{
			targetProcessId = _targetProcessId;
			_xaml ??= new XamlDiagnosticsSession(logger);
		}

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlApplyResult { Detail = "This session has no target process to inspect." };
		}

		return _xaml.ApplyEdits(pid, oldXaml, newXaml, filePath);
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
		int? targetProcessId;
		lock (_gate)
		{
			session = _session;
			targetProcessId = _targetProcessId;
		}

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
		// Nothing used to remove it at all, and the folders accumulated indefinitely -- each with a
		// copy of the provider and a grant to ALL APPLICATION PACKAGES. Whatever the target app still
		// holds open cannot go now, because detaching leaves that app running on purpose; the next
		// host to start sweeps the remainder once this pid is gone (#57).
		xaml?.Dispose();

		DisableUwpDebugging();
		return Task.CompletedTask;
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
