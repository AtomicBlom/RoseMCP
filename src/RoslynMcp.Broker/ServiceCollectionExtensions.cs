using Microsoft.Extensions.DependencyInjection;

using RoslynMcp.Broker.Tools;

namespace RoslynMcp.Broker;

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

		- Finding usages: roslyn_find_references, not grep. Grep matches comments, strings and
		  unrelated identifiers that share a name, and misses overrides, interface implementations
		  and aliases.
		- Renaming: roslyn_rename_symbol, not find-and-replace. It moves overrides, interface
		  implementations, partial declarations and cref references together, reports conflicts,
		  and returns a diff.
		- Checking code is valid: roslyn_diagnostics, not a build. It answers from a warm
		  compilation in milliseconds and needs no build.
		- Understanding a symbol: roslyn_symbol_info resolves the real signature, accessibility and
		  documentation rather than whatever the declaration text looks like.

		Source-generated code exists only inside the compilation. The compiler does not write it to
		disk, so no file read or search will ever find it. If a diagnostic names a file you cannot
		open, it is generated: read it with roslyn_read_generated_document using the hint name.

		No setup call is needed. Any tool taking a file path finds the enclosing solution itself, and
		with one solution nearby even the path is optional. The first call loads the solution and
		takes a few seconds; every call after that is fast, so there is no reason to batch around it.

		Edits made by other tools are picked up automatically before each call, so results are never
		stale and no refresh step exists. The one thing that does need roslyn_workspace_reload is
		rebuilding an analyzer or source generator, because loaded assemblies cannot be replaced.

		If answers look wrong or generated code seems missing, call roslyn_workspace_status. A
		degraded workspace returns plausible but incomplete results rather than errors.
		""";

	public static IMcpServerBuilder AddRoslynMcpBroker(
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
				server.ServerInfo = new() { Name = "roslyn-mcp", Version = "0.1.0" };
				server.ServerInstructions = Instructions;
			})
			.WithTools<BrokerTools>()
			.WithTools<BrokerAnalysisTools>();
	}
}
