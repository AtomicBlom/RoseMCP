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

	/// <summary>Attach a debugger to a running process and start a live-app session over it.</summary>
	public const string DebugAttach = "rose_debug_attach";

	/// <summary>Read new debug events (exceptions, log messages, module loads) from a session.</summary>
	public const string DebugEvents = "rose_debug_events";

	/// <summary>Detach the debugger and end a session, leaving the target running.</summary>
	public const string DebugDetach = "rose_debug_detach";

	/// <summary>List the live-app debug sessions the broker is supervising.</summary>
	public const string DebugList = "rose_debug_list";
}
