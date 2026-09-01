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
	public const string ListGeneratedDocuments = "rose_list_generated_documents";
	public const string ReadGeneratedDocument = "rose_read_generated_document";
	public const string RenameSymbol = "rose_rename_symbol";
	public const string MoveTypeToFile = "rose_move_type_to_file";
	public const string FormatDocuments = "rose_format";

	/// <summary>
	/// Worker-only. The broker calls this on connect to learn the process id, so it can sample
	/// memory from the outside and still get real numbers when the worker stops responding.
	/// </summary>
	public const string WorkerInfo = "rose_worker_info";
}
