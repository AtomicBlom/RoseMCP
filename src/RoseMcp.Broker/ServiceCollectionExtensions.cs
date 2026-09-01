using Microsoft.Extensions.DependencyInjection;

using RoseMcp.Broker.Tools;

namespace RoseMcp.Broker;

/// <summary>
/// The single registration path, shared by the console host and the tray app.
/// <para>
/// Having one of these is the reason the broker is a library. Two hosts each wiring up their own
/// services is two chances for them to disagree about what is loaded.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Sent to the client during initialize, which means the model sees it before it decides how to
	/// approach anything. That makes it the highest-leverage text in the server: tool descriptions
	/// are only read once a tool is already being considered, whereas this is what stops the reflex
	/// reach for grep in the first place. It is always in context, so it stays short and concrete.
	/// </summary>
	private const string Instructions = """
		Real C# semantics from a live Roslyn compilation of the user's solution, kept in sync with
		disk. For C# work, prefer these over text search and hand editing.

		- Locating something by name: rose_search_symbols, not a filename guess or a grep for the
		  declaration. It matches the abbreviations people type, and returns the position the other
		  tools want.
		- Finding usages: rose_find_references, not grep. Grep matches comments, strings and
		  unrelated identifiers that share a name, and misses overrides, interface implementations
		  and aliases.
		- Renaming: rose_rename_symbol, not find-and-replace. It moves overrides, interface
		  implementations, partial declarations and cref references together, reports conflicts,
		  and returns a diff. It also reports XAML that still names the old identifier, which it
		  does not change: markup is text to the compiler, so a broken binding builds and runs.
		- Splitting a file that declares several types: rose_move_type_to_file, not a read followed
		  by two writes. It carries the declaration across untouched and fixes the using directives
		  in both files, which hand-splitting gets wrong in a way that fails the build.
		- After writing or editing any C# file yourself: rose_format. Hand-written C# lands with
		  the wrong indentation and the wrong line endings, and where IDE0055 is an error that is a
		  failed build. This applies the repository's own .editorconfig, so it is not a matter of
		  taste, and it leaves multi-line string literals alone.
		- Fixing a diagnostic: rose_apply_code_fix, not editing each occurrence. The analyzers a
		  solution already has ship the fixes for their own rules, and Roslyn applies one across a
		  whole project or solution correctly where find-and-replace does not.
		  rose_list_code_fixes says what is available in a file.
		- Checking code is valid: rose_diagnostics, not a build. It answers from a warm
		  compilation in milliseconds and needs no build.
		- Understanding a symbol: rose_symbol_info resolves the real signature, accessibility,
		  documentation and declaration sites rather than whatever the declaration text looks like,
		  and says what the member overrides or implements.
		- Who implements or overrides something: rose_find_implementations. Grep cannot answer this
		  at all, since an implementation need not mention the interface anywhere near the member.

		Source-generated code exists only inside the compilation. The compiler does not write it to
		disk, so no file read or search will ever find it. If a diagnostic names a file you cannot
		open, it is generated: read it with rose_read_generated_document using the hint name.

		No setup call is needed. Any tool taking a file path finds the enclosing solution itself, and
		with one solution nearby even the path is optional. The first call loads the solution and
		takes a few seconds; every call after that is fast, so there is no reason to batch around it.

		Edits made by other tools are picked up automatically before each call, so results are never
		stale and no refresh step exists. The one thing that does need rose_workspace_reload is
		rebuilding an analyzer or source generator, because loaded assemblies cannot be replaced.

		If answers look wrong or generated code seems missing, call rose_workspace_status. A
		degraded workspace returns plausible but incomplete results rather than errors.

		If a whole solution looks broken -- thousands of errors, System.Object undefined, nothing
		resolving -- it is almost certainly loaded under the wrong MSBuild configuration rather than
		actually broken. rose_workspace_status reports the one in use and the ones the solution
		declares; rose_workspace_reload takes a different one.
		""";

	public static IMcpServerBuilder AddRoseMcpBroker(
		this IServiceCollection services,
		Action<BrokerOptions>? configure = null)
	{
		if (configure is not null) services.Configure(configure);

		// Singleton, so every session shares one set of workers. In http mode that is what lets a
		// reconnecting client reattach to an already-loaded solution rather than reload it.
		services.AddSingleton<WorkspaceManager>();

		return services
			.AddMcpServer(server =>
			{
				server.ServerInfo = new() { Name = "rose-mcp", Version = "0.1.0" };
				server.ServerInstructions = Instructions;
			})
			.WithTools<BrokerTools>()
			.WithTools<BrokerAnalysisTools>();
	}
}
