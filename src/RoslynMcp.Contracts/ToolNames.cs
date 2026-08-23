namespace RoslynMcp.Contracts;

/// <summary>
/// Tool names shared by the broker facade and the worker that implements them. The broker exposes
/// exactly these names; a worker exposes the same names minus the <c>workspace</c> argument, so
/// routing is a straight pass-through and a worker can be driven standalone by any MCP client.
/// </summary>
public static class ToolNames
{
	public const string WorkspaceOpen = "roslyn_workspace_open";
	public const string WorkspaceStatus = "roslyn_workspace_status";
	public const string WorkspaceReload = "roslyn_workspace_reload";
	public const string WorkspaceClose = "roslyn_workspace_close";
	public const string Diagnostics = "roslyn_diagnostics";
	public const string FindReferences = "roslyn_find_references";
	public const string SymbolInfo = "roslyn_symbol_info";
	public const string SearchSymbols = "roslyn_search_symbols";
	public const string ListGeneratedDocuments = "roslyn_list_generated_documents";
	public const string ReadGeneratedDocument = "roslyn_read_generated_document";
	public const string RenameSymbol = "roslyn_rename_symbol";
}
