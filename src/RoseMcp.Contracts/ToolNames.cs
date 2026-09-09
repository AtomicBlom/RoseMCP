namespace RoseMcp.Contracts;

/// <summary>
/// Every tool name in the system, in one place so a caller and its callee cannot spell one differently.
/// <para>
/// Three sets, and they are not the same set. The broker exposes the <c>rose_*</c> names an agent
/// sees. A worker exposes the Roslyn ones minus the <c>workspace</c> argument, so routing is a straight
/// pass-through and a worker can be driven standalone by any MCP client. The <c>rose_worker_info</c>
/// and <c>rose_live_app_*</c> names are host-internal: the broker calls them and declares none of them,
/// because a client offered one would be offered a tool with no session to run it against.
/// </para>
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

	public const string ChangeSignature = "rose_change_signature";

	public const string BuildFreshness = "rose_build_freshness";

	public const string AddUsing = "rose_add_using";

	public const string MoveMember = "rose_move_member";

	public const string Outline = "rose_outline";

	public const string ProjectGraph = "rose_project_graph";

	public const string DeleteMember = "rose_delete_member";

	public const string AddFile = "rose_add_file";

	public const string ReplaceDocComment = "rose_replace_doc_comment";

	public const string SetAttribute = "rose_set_attribute";

	public const string ResolveName = "rose_resolve_name";

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

	/// <summary>Live-app-host-only. The broker forwards this to apply a XAML change to the live tree.</summary>
	public const string LiveAppXamlApply = "rose_live_app_xaml_apply";

	/// <summary>Live-app-host-only. The broker forwards this to arm the interactive select-mode overlay.</summary>
	public const string LiveAppXamlSelectMode = "rose_live_app_xaml_select_mode";

	/// <summary>Live-app-host-only. The broker forwards this to read the element the user clicked.</summary>
	public const string LiveAppXamlSelection = "rose_live_app_xaml_selection";

	/// <summary>Live-app-host-only. The broker forwards this to clear the picked element and its mark.</summary>
	public const string LiveAppXamlDeselect = "rose_live_app_xaml_deselect";

	/// <summary>Live-app-host-only. The broker forwards this to select an element by its handle.</summary>
	public const string LiveAppXamlSelectElement = "rose_live_app_xaml_select_element";

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

	/// <summary>Live-edit a running XAML app: apply a file's changes to its live visual tree.</summary>
	public const string XamlApply = "rose_xaml_apply";

	/// <summary>
	/// Arming select mode, which the broker deliberately does not declare: the toolbar is how a person
	/// arms it, and an agent doing so can only end by asking them to click something anyway. The name
	/// is kept because the host still serves the verb and the integration tests drive it.
	/// </summary>
	public const string XamlSelectMode = "rose_xaml_select_mode";

	/// <summary>Read the element the user picked by clicking it in the running app.</summary>
	public const string XamlSelection = "rose_xaml_selection";

	/// <summary>Clear the picked element, and the mark drawn over the running app.</summary>
	public const string XamlDeselect = "rose_xaml_deselect";

	/// <summary>
	/// Select an element by its handle, reaching what a click cannot.
	/// <para>
	/// Spelled out rather than <c>rose_xaml_select</c>, because selecting an element and arming a mode
	/// that waits for a person to click one are different acts, and two names a suffix apart is how the
	/// wrong one gets called. Only this one is offered: arming is the person's own decision, made on
	/// the in-app toolbar, and an agent that arms it can only follow up by telling them to go and click
	/// something.
	/// </para>
	/// </summary>
	public const string XamlSelectElement = "rose_xaml_select_element";

	/// <summary>
	/// Which host-internal tool answers each live-app tool the broker declares, for the pairs where a
	/// name alone does not say.
	/// <para>
	/// The Roslyn half pairs by name -- a worker declares the same tool minus the workspace argument --
	/// so a parity test over it needs no map. The live-app half does not: the broker's
	/// <c>rose_debug_*</c> and <c>rose_xaml_*</c> names reach <c>rose_live_app_*</c> ones, and the four
	/// layers that spell each argument had already drifted twice with nothing to notice. This is what
	/// lets a test say the two ends declare the same arguments.
	/// </para>
	/// <para>
	/// Session lifecycle is absent on purpose: <c>rose_debug_attach</c>, <c>rose_debug_launch</c>,
	/// <c>rose_debug_launch_uwp</c> and <c>rose_debug_list</c> are the broker's own work -- starting,
	/// activating and enumerating host processes -- and no host tool corresponds to them. So is
	/// <c>rose_debug_detach</c>, which ends a session the broker owns rather than forwarding.
	/// </para>
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> LiveAppPairs = new Dictionary<string, string>
	{
		[DebugEvents] = LiveAppEvents,
		[DebugAddTracepoint] = LiveAppAddTracepoint,
		[DebugListTracepoints] = LiveAppListTracepoints,
		[DebugRemoveTracepoint] = LiveAppRemoveTracepoint,
		[DebugSetBreakpoint] = LiveAppSetBreakpoint,
		[DebugListBreakpoints] = LiveAppListBreakpoints,
		[DebugRemoveBreakpoint] = LiveAppRemoveBreakpoint,
		[DebugContinue] = LiveAppContinue,
		[DebugStep] = LiveAppStep,
		[DebugEvaluate] = LiveAppEvaluate,
		[XamlTree] = LiveAppXamlTree,
		[XamlProperties] = LiveAppXamlProperties,
		[XamlApply] = LiveAppXamlApply,
		[XamlSelection] = LiveAppXamlSelection,
		[XamlDeselect] = LiveAppXamlDeselect,
		[XamlSelectElement] = LiveAppXamlSelectElement,
	};
}
