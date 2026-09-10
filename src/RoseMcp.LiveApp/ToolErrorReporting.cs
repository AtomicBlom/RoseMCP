using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp;

/// <summary>
/// Lets a failure in this host explain itself instead of being replaced by a shrug.
/// <para>
/// The SDK turns an exception it does not recognise into "An error occurred invoking
/// 'rose_live_app_events'." and drops the message. Every boundary in this system is supposed to
/// have a filter that forwards the real one, and this boundary had none -- so a refusal this host
/// wrote, naming the event kind it did not know and listing the ones it does, reached the caller as
/// eleven words and the tool's own name.
/// </para>
/// <para>
/// A second copy of the worker's, rather than a shared one, because the only assembly both hosts
/// reference is <c>RoseMcp.Contracts</c> and that is deliberately a DTO assembly with no package
/// references at all. Nine lines in two places beats a dependency on the MCP hosting package from
/// the type library.
/// </para>
/// </summary>
public static class ToolErrorReporting
{
	/// <summary>
	/// Forwards the message and nothing else. The worker's copy names the solution it owns, because a
	/// caller cannot otherwise tell which workspace refused; this host does not need to, since the
	/// broker knows which session it sent to and one host serves one target.
	/// </summary>
	public static IMcpServerBuilder WithToolErrorMessages(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			try
			{
				return await next(context, cancellationToken);
			}
			catch (Exception exception) when (Explainable(exception))
			{
				throw new McpException(Named(context, exception), exception);
			}
		}));

	/// <summary>
	/// Whether the message is worth forwarding. Cancellation is not a failure, and anything already
	/// an <see cref="McpException"/> survives on its own and must not be wrapped twice.
	/// </summary>
	private static bool Explainable(Exception exception) =>
		exception is not OperationCanceledException
		and not McpException
		&& !string.IsNullOrWhiteSpace(exception.Message);

	/// <summary>
	/// The message to forward: the argument the caller got wrong where the binder refused one, and
	/// the exception's own words otherwise.
	/// <para>
	/// The binder's account of a malformed argument names a CLR type the caller never wrote and points
	/// at the root of the document, which is the one refusal on this surface that says nothing about
	/// what to send instead. The tool's own schema answers it, and asking only once the call has
	/// already been refused means a schema this cannot read costs nothing.
	/// </para>
	/// </summary>
	private static string Named(RequestContext<CallToolRequestParams> context, Exception exception)
	{
		if (exception is not JsonException) return exception.Message;
		if (context.MatchedPrimitive is not McpServerTool tool) return exception.Message;

		return ToolArgumentShape.Mismatch(tool.ProtocolTool.InputSchema, context.Params?.Arguments)
			?? exception.Message;
	}
}
