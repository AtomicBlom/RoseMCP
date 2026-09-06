using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

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
				throw new McpException(exception.Message, exception);
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
}
