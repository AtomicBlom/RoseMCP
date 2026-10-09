using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Lets a failure explain itself instead of being replaced by a shrug, at whichever MCP boundary
/// applies it.
/// </summary>
public static class ToolErrorReporting
{
	/// <summary>
	/// Forwards the real message of an exception the SDK would otherwise discard.
	/// <para>
	/// The SDK turns an exception it does not recognise into "An error occurred invoking
	/// 'rose_rename_symbol'." and drops the message, which is what a caller actually saw when a
	/// rename ran against the wrong workspace. Everything thrown on the way to here already knows
	/// what went wrong and says so -- a solution that has been deleted names its path, a worker
	/// relays its own tool's explanation -- and all of it was being discarded one frame from the
	/// caller.
	/// </para>
	/// <para>
	/// At the boundary rather than at each throw site, because the exception type carries meaning
	/// further in: the manager distinguishes a caller's mistake from a dead worker, and retry
	/// decisions turn on it. Only the wire needs the message.
	/// </para>
	/// <para>
	/// Public, and applied at every boundary rather than only where the tools are declared. A stdio
	/// session that relays to a tray declares no tools of its own and so had no filter, which is how
	/// a call could come back as the bare shrug this exists to remove -- a boundary is wherever an
	/// exception meets the SDK, not wherever a tool is written.
	/// </para>
	/// <para>
	/// A refusal the broker wrote keeps its words, and so does one a worker or a live-app host relayed,
	/// which that host's own filter has already put in the caller's terms; an exception that escaped a
	/// framework here is framed as the fault it is by <see cref="ToolFailure"/>. No CLR parameter name
	/// survives either path, and running the composition again over a relayed message changes nothing.
	/// </para>
	/// </summary>
	public static IMcpServerBuilder WithToolErrorMessages(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			try
			{
				return await next(context, cancellationToken);
			}
			catch (McpException exception) when (exception is not McpProtocolException)
			{
				// A refusal a tool already wrote for the wire keeps its words, and gains only what it
				// could not know: an argument the caller sent that the binder dropped before the tool ran.
				var named = Named(context, exception, exception.Message);
				if (named == exception.Message) throw;

				throw new McpException(named, exception);
			}
			catch (Exception exception) when (
				exception is not OperationCanceledException
				and not McpException
				&& !string.IsNullOrWhiteSpace(exception.Message))
			{
				var message = ToolFailure.Message(exception, context.Params?.Name ?? "The tool");

				throw new McpException(Named(context, exception, message), exception);
			}
		}));

	/// <summary>
	/// The message to forward: the argument the caller got wrong where the binder refused one,
	/// <paramref name="message"/> otherwise, and after either, any argument the call carried under a name
	/// the tool does not declare.
	/// <para>
	/// The binder's account of a malformed argument names a CLR type the caller never wrote and points
	/// at the root of the document, which is the one refusal on this surface that says nothing about
	/// what to send instead. The tool's own schema answers it, and asking only once the call has
	/// already been refused means a schema this cannot read costs nothing.
	/// </para>
	/// <para>
	/// The arguments read are the ones the tool bound, after the alias filter has rewritten the
	/// spellings it accepts, so a name the broker takes in place of a declared one is never reported
	/// as unknown. A worker's refusal reaches here as the broker's own exception, so it is the
	/// caller's original arguments that are named, not the ones the broker forwarded.
	/// </para>
	/// </summary>
	private static string Named(RequestContext<CallToolRequestParams> context, Exception exception, string message)
	{
		if (context.MatchedPrimitive is not McpServerTool tool) return ToolArgumentShape.WithoutParameterNames(message);

		return ToolArgumentShape.Refusal(
			message,
			exception is JsonException,
			tool.ProtocolTool.Name,
			tool.ProtocolTool.InputSchema,
			context.Params?.Arguments);
	}
}
