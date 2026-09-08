using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

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
	/// </summary>
	public static IMcpServerBuilder WithToolErrorMessages(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			try
			{
				return await next(context, cancellationToken);
			}
			catch (Exception exception) when (
				exception is not OperationCanceledException
				and not McpException
				&& !string.IsNullOrWhiteSpace(exception.Message))
			{
				throw new McpException(exception.Message, exception);
			}
		}));
}
