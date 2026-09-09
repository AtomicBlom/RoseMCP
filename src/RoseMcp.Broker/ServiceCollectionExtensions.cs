using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

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
	/// What the client is told during initialize. MinVer stamps it from the git tag at build time, so
	/// a version in a bug report names a commit. The build metadata after '+' is dropped -- it is the
	/// commit hash, which belongs in a log rather than in a handshake.
	/// </summary>
	private static readonly string ServerVersion =
		typeof(ServiceCollectionExtensions).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion.Split('+')[0]
		?? "0.0.0";

	/// <summary>
	/// Sent to the client during initialize, which means the model reads it before it decides how to
	/// approach anything. That makes it the highest-leverage text in the server: a tool description is
	/// read only once that tool is already a candidate, whereas this is what stops the reflex reach
	/// for grep in the first place.
	/// <para>
	/// A routing table, then the facts no description can carry. It was 11,340 characters of three to
	/// six lines of reasoning per tool, and the routing -- which tool for which situation -- was the
	/// first clause of each bullet, buried under it. The SDK's own guidance is that instructions
	/// "should not duplicate tool, prompt, or resource descriptions already exposed elsewhere", and
	/// every cut sentence still exists once, in the description of the tool it is about, where it is
	/// read at the moment of choosing.
	/// </para>
	/// <para>
	/// What earns a place under the table is what no description can say, because it is about the
	/// server rather than about one tool: that there is no setup call, that every result names the
	/// workspace that answered, that several solutions refuse rather than guess, that generated code
	/// is not on disk, and that thousands of errors about System.Object mean the wrong MSBuild
	/// configuration -- the one failure whose symptom points nowhere near its cause.
	/// </para>
	/// </summary>
	private const string Instructions = """
		Roslyn semantics over the user's C# solution, kept in sync with disk. For C#, reach for these
		before grep, find-and-replace or a text edit:

		- find a declaration: rose_search_symbols; what a type or a file contains: rose_outline
		- what a symbol is, and its code: rose_symbol_info with includeSource
		- usages: rose_find_references; implementors and overrides: rose_find_implementations
		- does it compile: rose_diagnostics -- a warm compilation, not a substitute for a build
		- start a file: rose_add_file, which picks the project and the namespace the folder implies
		- change a member: rose_replace_member, rose_add_member, rose_delete_member; just its body:
		  rose_replace_body, with find and replace for a change too small to re-emit one for; just
		  the prose or the attributes: rose_replace_doc_comment, rose_set_attribute
		- rename: rose_rename_symbol; add, remove or retype a parameter across every override,
		  implementation and call site: rose_change_signature; move a member between types:
		  rose_move_member; split a file: rose_move_type_to_file
		- imports: pass usings on any write, or rose_add_using for code that arrived another way;
		  which namespace a name needs: rose_resolve_name
		- analyzer fixes: rose_list_code_fixes then rose_apply_code_fix; formatting: rose_format
		- what depends on what: rose_project_graph; is bin/ this code: rose_build_freshness
		- source-generated code is not on disk at all: rose_list_generated_documents,
		  rose_read_generated_document

		Every write is addressed by name rather than by line, parsed before the file is opened,
		formatted to the repository's own .editorconfig, then compiled -- so the result says what the
		edit broke and there is no build in the loop. Grep matches comments, strings and same-named
		identifiers, and misses overrides and interface implementations.

		No setup call: every tool finds the enclosing solution from a path or the working directory.
		The first call loads it -- seconds usually, minutes for a very large one, which
		rose_workspace_open starts early and returns without waiting for. Every result names the
		workspace that answered and carries a revision, and a directory holding several solutions
		refuses and lists them rather than guessing. Edits by other tools are absorbed on the next
		call; only a rebuilt analyzer or generator needs rose_workspace_reload. If answers look
		wrong, ask rose_workspace_status -- thousands of errors about System.Object means the
		solution loaded under an MSBuild configuration it does not declare.
		""";

	/// <summary>
	/// Appended to <see cref="Instructions"/> only on Windows, because the tools it describes are only
	/// registered there. Instructions that name a tool the client cannot see are worse than silence:
	/// they spend context teaching an approach that fails on the first call.
	/// </summary>
	private const string DebuggingInstructions = """

		Debugging a running .NET process: rose_debug_attach takes a pid, rose_debug_launch an
		executable, rose_debug_launch_uwp an app user-model id; then rose_debug_events with kinds and
		waitSeconds waits for one thing rather than polling. rose_debug_add_tracepoint logs hits
		without stopping the app; rose_debug_set_breakpoint stops with a stack and locals and
		auto-continues after a timeout, so an unattended stop cannot wedge the app. At a stop,
		rose_debug_step, rose_debug_continue and rose_debug_evaluate (field chains only, and no
		debuggee code is run). rose_debug_detach leaves the target running.

		A running UWP or WinUI app is inspected and edited in place against the same session.
		rose_xaml_tree and rose_xaml_properties read the live visual tree and one element's real
		values, with where each was set. rose_xaml_apply live-edits it from the XAML file with no
		relaunch -- what Visual Studio calls XAML Hot Reload -- and the first call for a file records
		only a baseline, so make it before editing; the change lives on the objects in the tree
		rather than in the app's markup, so it is gone if the app rebuilds that part of the UI.
		rose_xaml_selection reads what the user clicked -- read it before asking them to point, since
		the toolbar is already in their app -- and rose_xaml_select_element picks one outright by
		handle or name, involving nobody.
		""";

	public static IMcpServerBuilder AddRoseMcpBroker(
		this IServiceCollection services,
		Action<BrokerOptions>? configure = null)
	{
		if (configure is not null) services.Configure(configure);

		// Singleton, so every session shares one set of workers. In http mode that is what lets a
		// reconnecting client reattach to an already-loaded solution rather than reload it.
		services.AddSingleton<WorkspaceManager>();

		// Live-app sessions are per running target, separate from the per-solution workers, but shared
		// across connections the same way and supervised the same way.
		services.AddSingleton<LiveAppSessionManager>();

		var builder = services
			.AddMcpServer(server =>
			{
				server.ServerInfo = new() { Name = "rose-mcp", Version = ServerVersion };
				server.ServerInstructions = OperatingSystem.IsWindows()
					? Instructions + DebuggingInstructions
					: Instructions;
			})
			.WithTools<BrokerTools>()
			.WithTools<BrokerAnalysisTools>();

		// The live-app debugger is ICorDebug and dbgshim, and RoseMcp.LiveApp is net10.0-windows, so
		// none of it is there on a Linux build. The launch sites already know that, but registration
		// did not, and a declared tool that cannot run is worse than an absent one: the caller only
		// finds out at the call, having already chosen the approach the tool implied was available.
		if (OperatingSystem.IsWindows()) builder = builder.WithTools<LiveAppDebugTools>();

		return builder
			.WithCallOrigin()
			.WithToolErrorMessages()
			.WithLeanListing();
	}

	/// <summary>
	/// Picks the two facts about the calling session out of the request and makes them available for the
	/// length of the call: the directory it lives in, out of <c>_meta</c>, and which session it is, from
	/// the transport.
	/// <para>
	/// A filter rather than tool parameters, so no tool declares either and no tool can forget one -- the
	/// same reasoning that puts attribution in one place. See <see cref="CallOrigin"/> for why the broker
	/// needs telling which directory, and <see cref="CallSession"/> for what owning a live-app session
	/// means.
	/// </para>
	/// </summary>
	private static IMcpServerBuilder WithCallOrigin(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			using var origin = CallOrigin.Use(OriginDirectory(context.Params));
			using var session = CallSession.Use(context.Server.SessionId);

			return await next(context, cancellationToken);
		}));

	/// <summary>
	/// Reads the origin directory a relay sent, ignoring anything malformed. A client is free to send
	/// whatever it likes here, and a bad value must fall back to inference rather than fail the call.
	/// </summary>
	private static string? OriginDirectory(CallToolRequestParams? parameters)
	{
		if (parameters?.Meta?[CallOrigin.MetaKey] is not JsonValue value) return null;

		return value.TryGetValue(out string? directory) && Directory.Exists(directory) ? directory : null;
	}

	/// <summary>
	/// Applies <see cref="ToolListing.Trim"/> at the one place every listing passes through, so no tool
	/// can be added that skips it.
	/// </summary>
	private static IMcpServerBuilder WithLeanListing(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, cancellationToken) =>
		{
			var result = await next(context, cancellationToken);

			foreach (var tool in result.Tools) ToolListing.Trim(tool);

			return result;
		}));
}
