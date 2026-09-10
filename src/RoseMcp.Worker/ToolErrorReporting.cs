using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Lets a tool's own explanation reach the caller instead of being replaced by a shrug.
/// <para>
/// The SDK turns an exception it does not recognise into "An error occurred invoking
/// 'rose_rename_symbol'." and drops the message. Every service here throws with a message that
/// says exactly what was wrong and what to pass instead -- which file matched nothing, how many
/// lines it actually has, what the type is really called -- and all of it was being discarded at
/// the boundary. A rename against the wrong workspace produced eleven words, none of them useful.
/// </para>
/// <para>
/// Converted here rather than at each throw site, because the exception type is doing real work
/// inside the worker: services distinguish a caller's mistake from an impossible state, and tests
/// assert on which is which. Only the wire needs the message.
/// </para>
/// </summary>
public static class ToolErrorReporting
{
	/// <summary>
	/// The solution this worker owns, named in every error it reports. Which workspace answered is
	/// the one thing a failing call could never say, and it is the thing most likely to be wrong --
	/// the failures worth explaining are mostly a file that belongs to some other solution.
	/// </summary>
	public static IMcpServerBuilder WithToolErrorMessages(this IMcpServerBuilder builder, string solutionPath) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			try
			{
				return await next(context, cancellationToken);
			}
			catch (Exception exception) when (Explainable(exception))
			{
				throw new McpException($"{Named(context, exception)} (workspace: {solutionPath})", exception);
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
