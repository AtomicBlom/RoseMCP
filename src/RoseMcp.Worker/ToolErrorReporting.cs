using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
	/// <para>
	/// An assembly the worker's own code could not load is explained rather than forwarded, and recorded
	/// on the workspace host so status says so too: the loader's message names a file, and says nothing of the
	/// worker being unable to answer that tool again until it is replaced.
	/// </para>
	/// <para>
	/// Anything else is told apart by <see cref="ToolFailure"/>: a refusal this worker wrote keeps its words,
	/// and an exception that escaped Roslyn or the BCL is framed as the fault it is, so it cannot be read as
	/// advice about the arguments. Either way no CLR parameter name reaches the caller, since the only names
	/// a caller knows are the tool's schema.
	/// </para>
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
				var message = Recorded(context, exception)?.Refusal
					?? ToolFailure.Message(exception, context.Params?.Name ?? "The tool");

				throw new McpException($"{Named(context, exception, message)} (workspace: {solutionPath})", exception);
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
	/// The assembly the call failed to load for this worker's own code, recorded on the workspace host, or null when
	/// the failure was anything else.
	/// </summary>
	private static AssemblyLoadFault? Recorded(RequestContext<CallToolRequestParams> context, Exception exception)
	{
		var fault = AssemblyLoadFault.From(exception, context.Params?.Name ?? "The tool");
		if (fault is null) return null;

		// Said rather than skipped when there is no host to record on: unrecorded, the workspace goes on calling
		// itself healthy, which is the failure this exists to prevent.
		if (context.Services?.GetService<WorkspaceHost>() is { } host)
		{
			host.RecordAssemblyLoadFault(fault);
		}
		else
		{
			context.Services?.GetService<ILoggerFactory>()
				?.CreateLogger(typeof(ToolErrorReporting).FullName!)
				.LogWarning(
					"{Tool} failed to load {Assembly}, and there is no workspace host to record it on, so status will not say so.",
					fault.Tool,
					fault.Assembly);
		}

		return fault;
	}

	/// <summary>
	/// The message to forward: the argument the caller got wrong where the binder refused one,
	/// <paramref name="message"/> otherwise -- the exception's own words, or the boundary's explanation
	/// of them -- and after either, any argument the call carried under a name the tool does not
	/// declare. Composed by <see cref="ToolArgumentShape.Refusal"/>, so every
	/// boundary words it the same way.
	/// <para>
	/// The binder's account of a malformed argument names a CLR type the caller never wrote and points
	/// at the root of the document, which is the one refusal on this surface that says nothing about
	/// what to send instead. The tool's own schema answers it, and asking only once the call has
	/// already been refused means a schema this cannot read costs nothing.
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
