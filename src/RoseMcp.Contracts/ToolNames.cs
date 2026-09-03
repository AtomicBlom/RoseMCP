namespace RoseMcp.Contracts;

/// <summary>
/// Tool names shared by the broker facade and the worker that implements them. The broker exposes
/// exactly these names; a worker exposes the same names minus the <c>workspace</c> argument, so
/// routing is a straight pass-through and a worker can be driven standalone by any MCP client.
/// </summary>
public static class ToolNames
{
	public const string WorkspaceOpen = "rose_workspace_open";
	public const string WorkspaceStatus = "rose_workspace_status";
	public const string WorkspaceReload = "rose_workspace_reload";
	public const string WorkspaceClose = "rose_workspace_close";
	public const string Diagnostics = "rose_diagnostics";
	public const string FindReferences = "rose_find_references";
	public const string SymbolInfo = "rose_symbol_info";
	public const string SearchSymbols = "rose_search_symbols";
	public const string FindImplementations = "rose_find_implementations";
	public const string ListGeneratedDocuments = "rose_list_generated_documents";
	public const string ReadGeneratedDocument = "rose_read_generated_document";
	public const string RenameSymbol = "rose_rename_symbol";
	public const string MoveTypeToFile = "rose_move_type_to_file";
	public const string FormatDocuments = "rose_format";
	public const string ListCodeFixes = "rose_list_code_fixes";
	public const string ApplyCodeFix = "rose_apply_code_fix";
	public const string ReplaceMember = "rose_replace_member";
	public const string ReplaceBody = "rose_replace_body";
	public const string AddMember = "rose_add_member";

	/// <summary>
	/// Worker-only. The broker calls this on connect to learn the process id, so it can sample
	/// memory from the outside and still get real numbers when the worker stops responding.
	/// </summary>
	public const string WorkerInfo = "rose_worker_info";

	/// <summary>
	/// Live-app-host-only. The broker calls this on connect to learn the host's process id, the
	/// architecture it launched as, and whether it established its target.
	/// </summary>
	public const string LiveAppInfo = "rose_live_app_info";

	/// <summary>
	/// Live-app-host-only. The broker forwards this to read the host's buffered debug events; the
	/// agent-facing counterpart is <see cref="DebugEvents"/>.
	/// </summary>
	public const string LiveAppEvents = "rose_live_app_events";

	/// <summary>
	/// Live-app-host-only. The broker calls this before closing the host, so the debugger detaches
	/// while the host is still alive and the target is left running rather than taken down with it.
	/// </summary>
	public const string LiveAppDetach = "rose_live_app_detach";

	/// <summary>Live-app-host-only. The broker forwards this to add a tracepoint.</summary>
	public const string LiveAppAddTracepoint = "rose_live_app_add_tracepoint";

	/// <summary>Live-app-host-only. The broker forwards this to list tracepoints.</summary>
	public const string LiveAppListTracepoints = "rose_live_app_list_tracepoints";

	/// <summary>Live-app-host-only. The broker forwards this to remove a tracepoint.</summary>
	public const string LiveAppRemoveTracepoint = "rose_live_app_remove_tracepoint";

	/// <summary>Live-app-host-only. The broker forwards this to set a stopping breakpoint.</summary>
	public const string LiveAppSetBreakpoint = "rose_live_app_set_breakpoint";

	/// <summary>Live-app-host-only. The broker forwards this to list stopping breakpoints.</summary>
	public const string LiveAppListBreakpoints = "rose_live_app_list_breakpoints";

	/// <summary>Live-app-host-only. The broker forwards this to remove a stopping breakpoint.</summary>
	public const string LiveAppRemoveBreakpoint = "rose_live_app_remove_breakpoint";

	/// <summary>Live-app-host-only. The broker forwards this to resume a target held at a breakpoint.</summary>
	public const string LiveAppContinue = "rose_live_app_continue";

	/// <summary>Live-app-host-only. The broker forwards this to step a held target.</summary>
	public const string LiveAppStep = "rose_live_app_step";

	/// <summary>Live-app-host-only. The broker forwards this to evaluate a field-access expression at a stop.</summary>
	public const string LiveAppEvaluate = "rose_live_app_evaluate";

	/// <summary>
	/// Live-app-host-only. The broker forwards this to inject the XAML diagnostics provider into the
	/// target and read a snapshot of its live visual tree.
	/// </summary>
	public const string LiveAppXamlTree = "rose_live_app_xaml_tree";

	/// <summary>Live-app-host-only. The broker forwards this to read one element's XAML properties.</summary>
	public const string LiveAppXamlProperties = "rose_live_app_xaml_properties";

	/// <summary>Live-app-host-only. The broker forwards this to diff two XAML versions and apply the edits live.</summary>
	public const string LiveAppXamlApply = "rose_live_app_xaml_apply";

	/// <summary>Live-app-host-only. The broker forwards this to arm the interactive select-mode overlay.</summary>
	public const string LiveAppXamlSelectMode = "rose_live_app_xaml_select_mode";

	/// <summary>Live-app-host-only. The broker forwards this to read the element the user clicked.</summary>
	public const string LiveAppXamlSelection = "rose_live_app_xaml_selection";

	/// <summary>Attach a debugger to a running process and start a live-app session over it.</summary>
	public const string DebugAttach = "rose_debug_attach";

	/// <summary>Launch a .NET executable under the debugger and start a session over it from startup.</summary>
	public const string DebugLaunch = "rose_debug_launch";

	/// <summary>Activate a packaged (UWP) app under the debugger by its app user-model id.</summary>
	public const string DebugLaunchUwp = "rose_debug_launch_uwp";

	/// <summary>Read new debug events (exceptions, log messages, module loads) from a session.</summary>
	public const string DebugEvents = "rose_debug_events";

	/// <summary>Detach the debugger and end a session, leaving the target running.</summary>
	public const string DebugDetach = "rose_debug_detach";

	/// <summary>List the live-app debug sessions the broker is supervising.</summary>
	public const string DebugList = "rose_debug_list";

	/// <summary>Add a tracepoint: a breakpoint that logs and auto-continues without pausing.</summary>
	public const string DebugAddTracepoint = "rose_debug_add_tracepoint";

	/// <summary>List a session's tracepoints and whether each is bound.</summary>
	public const string DebugListTracepoints = "rose_debug_list_tracepoints";

	/// <summary>Remove a tracepoint by id.</summary>
	public const string DebugRemoveTracepoint = "rose_debug_remove_tracepoint";

	/// <summary>Set a stopping breakpoint that holds the target on hit, with an auto-continue timeout.</summary>
	public const string DebugSetBreakpoint = "rose_debug_set_breakpoint";

	/// <summary>List a session's stopping breakpoints and whether each is bound.</summary>
	public const string DebugListBreakpoints = "rose_debug_list_breakpoints";

	/// <summary>Remove a stopping breakpoint by id.</summary>
	public const string DebugRemoveBreakpoint = "rose_debug_remove_breakpoint";

	/// <summary>Resume a target that is held at a stopping breakpoint.</summary>
	public const string DebugContinue = "rose_debug_continue";

	/// <summary>Step a target held at a breakpoint: in, over, or out.</summary>
	public const string DebugStep = "rose_debug_step";

	/// <summary>Evaluate a field-access expression against a stopped frame, without running debuggee code.</summary>
	public const string DebugEvaluate = "rose_debug_evaluate";

	/// <summary>Read a snapshot of a live app's XAML visual tree by injecting the diagnostics provider.</summary>
	public const string XamlTree = "rose_xaml_tree";

	/// <summary>Read one element's XAML properties, with provenance and source location.</summary>
	public const string XamlProperties = "rose_xaml_properties";

	/// <summary>Hot-reload a running XAML app by diffing two versions and applying the edits live.</summary>
	public const string XamlApply = "rose_xaml_apply";

	/// <summary>Enter interactive select mode: the next click in the app picks that element.</summary>
	public const string XamlSelectMode = "rose_xaml_select_mode";

	/// <summary>Read the element the user picked by clicking it in the running app.</summary>
	public const string XamlSelection = "rose_xaml_selection";
}
