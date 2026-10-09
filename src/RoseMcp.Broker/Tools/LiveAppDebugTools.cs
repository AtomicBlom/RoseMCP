using System.ComponentModel;
using System.Diagnostics;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;
using RoseMcp.Settings;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// The agent-facing debugging surface. Each tool drives a per-target live-app session the broker
/// supervises, the debugging counterpart to the per-solution workspace tools: attach or launch, watch
/// what the target throws and logs, set tracepoints and breakpoints, step, evaluate a field chain in a
/// stopped frame, read and edit the running visual tree, and detach.
/// <para>
/// Every one of them but the four that start or list a session takes a session id, and reaches it
/// through <see cref="LiveAppSessionManager"/>, which serves only the calling client its own.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LiveAppDebugTools(
	LiveAppSessionManager sessions,
	IInspectorPresenter inspector,
	CallerPaths paths)
{
	[McpServerTool(
		Name = ToolNames.DebugAttach,
		Title = "Attach a debugger to a process",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Attach a debugger to a running .NET process by its id and start a session over it, without "
			+ "Visual Studio and without pausing it. The target keeps running; exceptions, log messages "
			+ "and module loads are captured for rose_debug_events to read. Local, same-user processes "
			+ "only. Returns the session id to pass to rose_debug_events and rose_debug_detach.")]
	public async Task<LiveAppSessionSummary> AttachAsync(
		[Description(ToolDescriptions.ProcessIdArgument)]
		int processId,
		[Description(ToolDescriptions.ShowInspectorArgument)]
		InspectorVisibility showInspector = InspectorVisibility.UserPreference,
		CancellationToken cancellationToken = default)
	{
		LocalAttachPolicy.EnsureAttachable(processId);

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.AttachProcess,
			ProcessId = processId,
			Description = DescribeProcess(processId),
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			// The host is alive but could not attach; reclaim it and tell the caller why.
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not attach to pid {processId}.");
		}

		ShowInspectorIfWanted(session, showInspector);

		return session.Describe();
	}

	/// <summary>
	/// Opens the inspector on a session that has just started, when the caller and the machine's
	/// preference agree it should be.
	/// <para>
	/// Reported in the session's event stream rather than thrown or returned. The session is the
	/// caller's result and it is already running: refusing the attach because a window could not
	/// open would throw away the work that mattered, and saying nothing would leave somebody who
	/// asked for a window staring at a screen with no window and no reason.
	/// </para>
	/// </summary>
	private void ShowInspectorIfWanted(LiveAppSession session, InspectorVisibility asked)
	{
		if (!InspectorRequest.Wanted(asked, RoseSettingsFile.Read())) return;

		var refused = inspector.CanShow
			? inspector.Show(session.SessionId, session.Describe().TargetProcessId)
			: inspector.Obstacle;

		if (refused is not null) session.Note($"The inspector was asked for and did not open: {refused}");
	}

	[McpServerTool(
		Name = ToolNames.DebugLaunch,
		Title = "Launch a process under the debugger",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Launch a local .NET executable under the debugger and start a session over it from startup, so "
			+ "its earliest module loads and exceptions are captured -- which attaching after the fact "
			+ "cannot see. The program runs as the current user, the same as launching it yourself. Set "
			+ "breakpoints once it is running the way you would after an attach. Returns the session id for "
			+ "rose_debug_events and rose_debug_detach. Detaching leaves it running.")]
	public async Task<LiveAppSessionSummary> LaunchAsync(
		[Description(ToolDescriptions.ExecutablePathArgument)] string executablePath,
		[Description(ToolDescriptions.LaunchArgumentsArgument)] string? arguments = null,
		[Description(ToolDescriptions.ShowInspectorArgument)]
		InspectorVisibility showInspector = InspectorVisibility.UserPreference,
		CancellationToken cancellationToken = default)
	{
		// Measured from the calling session, like every other path argument: two checkouts of one
		// repository hold the same bin/Debug path, and launching the other one's build is a session
		// spent debugging code that is not the code being edited.
		var fullPath = paths.Of(executablePath)?.Value ?? executablePath;

		if (!File.Exists(fullPath)) throw new McpException($"No executable at {fullPath}.");

		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchExecutable,
			ExecutablePath = fullPath,
			Arguments = arguments,
			Description = $"{Path.GetFileName(fullPath)} (launched)",
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not launch {fullPath}.");
		}

		ShowInspectorIfWanted(session, showInspector);

		return session.Describe();
	}

	[McpServerTool(
		Name = ToolNames.DebugLaunchUwp,
		Title = "Launch a UWP app under the debugger",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = true,
		UseStructuredContent = true)]
	[Description(
		"Activate a packaged (UWP) app under the debugger by its app user-model id (PackageFamilyName!App) "
			+ "and start a session over it. The package is put in debug mode (no suspension, no activation "
			+ "timeout) and activated, then attached -- so a classic UWP app, which has no ARM64 runtime and "
			+ "runs x64 emulated, is debugged through the x64 host automatically. The app must already be "
			+ "deployed. Detaching leaves it running and lifts debug mode. Returns the session id.")]
	public async Task<LiveAppSessionSummary> LaunchUwpAsync(
		[Description(ToolDescriptions.AppUserModelIdArgument)] string appUserModelId,
		[Description(ToolDescriptions.ShowInspectorArgument)]
		InspectorVisibility showInspector = InspectorVisibility.UserPreference,
		CancellationToken cancellationToken = default)
	{
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.LaunchUwp,
			AppUserModelId = appUserModelId,
			Description = $"{appUserModelId} (UWP)",
		};

		var session = await sessions.StartAsync(target, cancellationToken);
		var summary = session.Describe();

		if (summary.State == LiveAppSessionState.Faulted)
		{
			await sessions.CloseAsync(session.SessionId, cancellationToken);
			throw new McpException(summary.Detail ?? $"Could not launch {appUserModelId}.");
		}

		ShowInspectorIfWanted(session, showInspector);

		return session.Describe();
	}

	[McpServerTool(
		Name = ToolNames.DebugEvents,
		Title = "Read debug events",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Debug events captured since a cursor: first-chance and unhandled exceptions, Debugger.Log "
			+ "messages, module loads, breakpoint hits and step completions. Pass the returned "
			+ "nextCursor as 'after' next time to get only what is new; a cursor below "
			+ "oldestAvailable means the buffer dropped events between them. Filter with 'kinds' -- a "
			+ "freshly started app produces hundreds of ModuleLoaded events, and asking for LogMessage "
			+ "or ExceptionFirstChance alone is the difference between a readable answer and one that "
			+ "has to be written to a file. Use waitSeconds with kinds to wait for one thing, such as "
			+ "BreakpointHit, rather than calling this in a loop. Pass 'sequence' to fetch one event "
			+ "whole when a page came back truncated. A page cut at its limit counts what lies past it by "
			+ "kind and exceptionType, each a value those filters take.")]
	public async Task<LiveDebugEventPage> EventsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.AfterSequenceArgument)]
		long after = 0,
		[Description(ToolDescriptions.EventKindsArgument)]
		string[]? kinds = null,
		[Description(ToolDescriptions.MaxEventsArgument)]
		int limit = 500,
		[Description(ToolDescriptions.WaitSecondsArgument)]
		int waitSeconds = 0,
		[Description(ToolDescriptions.EventSequenceArgument)]
		long? sequence = null,
		[Description(ToolDescriptions.EventExceptionTypeArgument)]
		string? exceptionType = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadEventsAsync(after, kinds, limit, waitSeconds, sequence, cancellationToken, exceptionType);
	}

	[McpServerTool(
		Name = ToolNames.DebugDetach,
		Title = "Detach and end a session",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Detach the debugger and end the session, leaving the target process running exactly as before. "
			+ "Use this to stop watching a process without killing it; a debugger that is simply abandoned "
			+ "would otherwise risk taking the target down with it, which detaching avoids. It fails rather "
			+ "than reporting success if the debugger could not be detached, since that is the one outcome "
			+ "where the target is at risk.")]
	public async Task<LiveSessionDetached> DetachAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		// Held before the close, because closing forgets it, and its answer to "did the detach
		// actually happen" is the only thing that makes a result saying it did true rather than habitual.
		var session = sessions.Find(sessionId);

		var closed = await sessions.CloseAsync(sessionId, cancellationToken);
		if (!closed)
		{
			// A session whose host died was dropped, and is said to be, so a caller holding its id does not
			// read the answer as a wrong id. Only the caller that started it is told.
			var dropped = sessions.FindDropped(sessionId);

			return new LiveSessionDetached
			{
				SessionId = sessionId,
				Detached = false,
				Detail = dropped?.Explain(sessions.UtcNow, StartAgain),
			};
		}

		if (session?.DetachFailure is { Length: > 0 } failure)
		{
			// Nothing here can say the target survived, so the message must not lead with that: a failed
			// detach is no evidence about the target's state, and the target can be gone already.
			throw new McpException(
				$"The session is closed, but the debugger may still be attached to the target: {failure} "
					+ "Nothing is watching the target any more, and whether it is still running is not known; "
					+ "check the process before relying on it.");
		}

		return new LiveSessionDetached { SessionId = sessionId, Detached = true };
	}

	[McpServerTool(
		Name = ToolNames.DebugList,
		Title = "List debug sessions",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List the live-app debug sessions the broker is supervising, each with its session id, target, "
			+ "architecture, state, and process ids. Use it to recover a session id you did not keep from "
			+ "rose_debug_attach, or to see what is currently attached before starting another session.")]
	public LiveAppSessionList List() => new() { Sessions = sessions.DescribeOwned() };

	[McpServerTool(
		Name = ToolNames.DebugAddTracepoint,
		Title = "Add tracepoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Add tracepoints at methods by name: breakpoints that log and immediately continue, so they never "
			+ "freeze the target the way a stopping breakpoint would -- the right default for a turn-based "
			+ "agent. Each hit appears in rose_debug_events, carrying the values its message interpolated as "
			+ "data as well as in the line. Prefer this over adding logging statements and rebuilding, which "
			+ "needs a source edit and a restart to see anything. Each entry gets its own status: one whose "
			+ "module has not loaded binds when it does, and one whose name did not resolve says why and "
			+ "will not bind.")]
	public async Task<LiveTracepointBatch> AddTracepointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.TracepointsArgument)]
		AddTracepointRequest[] tracepoints,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.AddTracepointsAsync(tracepoints, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugListTracepoints,
		Title = "List tracepoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List a session's tracepoints, each with its id, location, hit count, and whether it is bound "
			+ "yet, and why not: a module still to load, or a name that did not resolve.")]
	public async Task<LiveTracepointList> ListTracepointsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ListTracepointsAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugRemoveTracepoint,
		Title = "Remove tracepoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Remove tracepoints by id and return the remaining set. Use it to stop the tracepoints once you "
			+ "have seen what you needed, rather than leaving hot-path logs running all session.")]
	public async Task<LiveTracepointRemoval> RemoveTracepointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.TracepointIdsArgument)] string[] tracepointIds,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.RemoveTracepointsAsync(tracepointIds, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugSetBreakpoint,
		Title = "Set stopping breakpoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Set stopping breakpoints at methods by name: on hit one pauses the target and records the stop "
			+ "with its call stack in rose_debug_events, so you can see how execution got there. The target "
			+ "stays frozen until rose_debug_continue, or until an auto-continue safety timeout (default 30s) "
			+ "fires so an unattended stop cannot wedge the app -- so read the events and continue promptly. "
			+ "Each entry gets its own status, and why when unbound. For non-invasive logging that never pauses, prefer rose_debug_add_tracepoint.")]
	public async Task<LiveBreakpointBatch> SetBreakpointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.BreakpointsArgument)]
		SetBreakpointRequest[] breakpoints,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.SetBreakpointsAsync(breakpoints, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugListBreakpoints,
		Title = "List stopping breakpoints",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"List a session's stopping breakpoints, each with its id, location, hit count, auto-continue "
			+ "timeout, and whether it is bound yet, and why not: a module still to load, or "
			+ "a name that did not resolve.")]
	public async Task<LiveBreakpointList> ListBreakpointsAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ListBreakpointsAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugRemoveBreakpoint,
		Title = "Remove stopping breakpoints",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Remove stopping breakpoints by id and return the remaining set. Use it once you have seen what "
			+ "you needed; removing one the target is currently held at does not itself resume -- call "
			+ "rose_debug_continue for that.")]
	public async Task<LiveBreakpointRemoval> RemoveBreakpointAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.BreakpointIdsArgument)] string[] breakpointIds,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.RemoveBreakpointsAsync(breakpointIds, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.DebugContinue,
		Title = "Continue from a breakpoint",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Resume a target that is held at a stopping breakpoint, so it keeps running. Call this after you "
			+ "have read the stop and its stack from rose_debug_events; it is a no-op if nothing is "
			+ "currently stopped, which is safe to call speculatively.")]
	public async Task<LiveContinued> ContinueAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		var resumed = await session.ResumeAsync(cancellationToken);

		return new LiveContinued
		{
			SessionId = sessionId,
			Continued = resumed.Continued,
			Detail = resumed.Detail,
			Cursor = resumed.Cursor,
		};
	}

	[McpServerTool(
		Name = ToolNames.DebugStep,
		Title = "Step",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Step a target that is held at a breakpoint: 'in' steps into calls, 'over' runs them without "
			+ "descending, 'out' runs to the caller. The step resumes the target briefly and then holds it "
			+ "again at the new location, which arrives as a StepComplete event in rose_debug_events with a "
			+ "fresh stack and locals. It is a no-op if nothing is currently stopped. Line granularity "
			+ "needs a PDB; without one, a step lands at the runtime's own step boundaries.")]
	public async Task<LiveStepped> StepAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.StepModeArgument)] string mode = "over",
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		var stepped = await session.StepDetailedAsync(mode, cancellationToken);

		return new LiveStepped
		{
			SessionId = sessionId,
			Mode = mode,
			Stepped = stepped.Continued,
			Detail = stepped.Detail,
			Cursor = stepped.Cursor,
		};
	}

	[McpServerTool(
		Name = ToolNames.DebugEvaluate,
		Title = "Evaluate an expression at a stop",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Evaluates a field-access chain against a target held at a breakpoint or a step: an argument "
			+ "or local name, then .field into the object graph. It reads fields from memory and runs "
			+ "none of the debuggee's own code, so it never hangs or changes the target -- property "
			+ "getters and method calls are deliberately not evaluated. Only valid while stopped. "
			+ "Arguments and locals go by the names the breakpoint's recorded frame reports: the names "
			+ "the source declares where the module has symbols beside it, and local_0, local_1 in slot "
			+ "order where it has none. Returns the value and its type, or why it did not resolve. A "
			+ "string is cut short unless maxLength says otherwise, and fullLength says when it was.")]
	public async Task<LiveEvaluation> EvaluateAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.EvaluateExpressionArgument)] string expression,
		[Description(ToolDescriptions.EvaluateMaxLengthArgument)] int? maxLength = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.EvaluateAsync(expression, maxLength, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlTree,
		Title = "Read the live XAML visual tree",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads a snapshot of a running app's live XAML visual tree: a flat list of elements, each with "
			+ "a stable handle, its parent handle and child index to rebuild the tree from, its type, "
			+ "its x:Name where it has one, and an address that names it even where it has not. It "
			+ "injects a diagnostics provider into the target and enumerates on the app's UI thread. "
			+ "The target must be a XAML app (UWP or WinUI); one with no XAML UI, or a build with no "
			+ "provider, comes back with a detail and no nodes rather than failing.")]
	public async Task<LiveXamlTree> XamlTreeAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlRootArgument)] string? root = null,
		[Description(ToolDescriptions.XamlOffsetArgument)] int offset = 0,
		[Description(ToolDescriptions.XamlLimitArgument)] int limit = 0,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlTreeAsync(root, offset, limit, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlProperties,
		Title = "Read a XAML element's properties",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads one element's XAML properties. Each comes with its value, type and provenance -- Local, "
			+ "Style, Inherited, Animation, Default -- so what the markup sets can be told from a "
			+ "framework default, and with the file and line that set it where the app carries source "
			+ "info. This is the bridge from a live element to its XAML source. Set properties only by "
			+ "default; includeDefaults gives the full set. One caveat, and it is measured: reading an "
			+ "element brings its untouched collection properties into existence, so a second read "
			+ "reports a few more as Local than the first -- on a TextBlock, Inlines, TextHighlighters "
			+ "and SelectionHighlightColor. The first read is the accurate one; do not treat properties "
			+ "that appear between two reads as something an edit did.")]
	public async Task<LiveXamlProperties> XamlPropertiesAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlElementArgument)] string element,
		[Description(ToolDescriptions.IncludeDefaultsArgument)]
		bool includeDefaults = false,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlPropertiesAsync(
			await session.ResolveElementAsync(element, cancellationToken), includeDefaults, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlApply,
		Title = "Live-edit XAML",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Applies a XAML file's current text to the running app's visual tree, no relaunch -- what "
			+ "Visual Studio calls XAML Hot Reload. Pass filePath; the session diffs it against what it "
			+ "last sent, so the loop is edit, apply, edit, apply. Reach for it instead of rebuilding and "
			+ "relaunching to see a layout or a colour change. The first call for a file records a "
			+ "baseline and applies nothing: make it before editing, or pass oldXaml with filePath. "
			+ "Property changes, added and removed elements and changed resources apply, to named "
			+ "and unnamed elements alike -- an unnamed one by the address rose_xaml_tree and "
			+ "rose_xaml_selection report. For markup not on disk, pass oldXaml and newXaml. Read the "
			+ "notes: they list edits worked out but not applied, and a failed edit is not retried by "
			+ "the next apply, since re-sending an added element would add a second copy. The change "
			+ "lives on the objects in the tree rather than in the app's markup, so it is gone if the "
			+ "app rebuilds that part of the UI.")]
	public async Task<LiveXamlApplyResult> XamlApplyAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlFilePathArgument), ArgumentAlias("file"), ArgumentAlias("path")]
		string? filePath = null,
		[Description(ToolDescriptions.XamlOldMarkupArgument)]
		string? oldXaml = null,
		[Description(ToolDescriptions.XamlNewMarkupArgument)]
		string? newXaml = null,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ApplyXamlAsync(oldXaml, newXaml, paths.Of(filePath)?.Value, cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelection,
		Title = "Read the selected XAML element",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Reads the element the user picked by clicking it in the running app, with the whole stack "
			+ "under that click, topmost first -- a click usually lands on an unnamed part of a "
			+ "template, so the container above it is often what was meant. Each carries a handle and an "
			+ "address, either of which rose_xaml_properties and rose_xaml_apply take. Read it before "
			+ "asking the user to point at anything: they can arm select mode themselves from the "
			+ "in-app toolbar, so an answer may already be waiting. Nothing picked reads as empty "
			+ "rather than failing.")]
	public async Task<LiveXamlSelection> XamlSelectionAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ReadXamlSelectionAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlDeselect,
		Title = "Clear the selected XAML element",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Clears the picked element and the mark drawn over the running app. "
			+ "Call it when you have finished with a selection: the mark stays on screen until "
			+ "something clears it, and a person left looking at it has no way to know the tool is done "
			+ "with it.")]
	public async Task<LiveXamlSelection> XamlDeselectAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);
		return await session.ClearXamlSelectionAsync(cancellationToken);
	}

	[McpServerTool(
		Name = ToolNames.XamlSelectElement,
		Title = "Select a XAML element by handle",
		ReadOnly = false,
		Destructive = false,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(
		"Selects one element without a click, reaching what a click cannot: something behind another "
			+ "element, or a part of a template a person cannot hit. Takes a handle, an x:Name as "
			+ "#name, or the address rose_xaml_tree reports, and answers with the same shape "
			+ "rose_xaml_selection does, so the two are interchangeable from there on. It marks the "
			+ "element in the app, so the user can see what was picked. A name matching several "
			+ "elements is refused rather than one of them chosen.")]
	public async Task<LiveXamlSelection> XamlSelectElementAsync(
		[Description(ToolDescriptions.SessionArgument)] string sessionId,
		[Description(ToolDescriptions.XamlElementArgument)] string element,
		CancellationToken cancellationToken = default)
	{
		var session = Require(sessionId);

		return await session.SelectXamlElementAsync(
			await session.ResolveElementAsync(element, cancellationToken), cancellationToken);
	}

	/// <summary>
	/// The caller's session with that id, or a refusal saying why there is none: that its host died and
	/// it was dropped, where this caller started it and that is so, and otherwise that no such session is
	/// open for this caller.
	/// </summary>
	/// <exception cref="McpException">No open session of this caller's has that id.</exception>
	private LiveAppSession Require(string sessionId)
	{
		if (sessions.Find(sessionId) is { } session) return session;

		if (sessions.FindDropped(sessionId) is { } dropped)
		{
			throw new McpException(dropped.Explain(sessions.UtcNow, StartAgain));
		}

		throw new McpException(
			$"No debug session '{sessionId}' is open for this client. rose_debug_list names the ones there "
				+ "are. A session another client of this broker started belongs to it and is not reachable "
				+ "from here.");
	}

	/// <summary>What a caller whose session was dropped does to debug the target again.</summary>
	private const string StartAgain =
		$"Start a new session with {ToolNames.DebugAttach}, {ToolNames.DebugLaunch} or {ToolNames.DebugLaunchUwp} "
			+ "to debug the target again.";

	private static string DescribeProcess(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return $"{process.ProcessName} (pid {processId})";
		}
		catch (Exception)
		{
			return $"pid {processId}";
		}
	}
}
