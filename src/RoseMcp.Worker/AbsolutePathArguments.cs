using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Refuses a path argument that is not absolute. <see cref="PathArguments"/> holds the rule and the
/// reason; this is the worker's end of it.
/// <para>
/// A relative path resolves here against this process's working directory, which is its solution's
/// root -- so a call meant for another checkout of the same repository names a real file under this
/// one, and the edit applies, verifies and reports success. Refusing is what turns the only failure
/// on this surface invisible to the caller's own working copy into a sentence.
/// </para>
/// <para>
/// A filter rather than a check in each tool, for the reason every cross-cutting rule here is one:
/// the tool added next is the one that forgets.
/// </para>
/// </summary>
public static class AbsolutePathArguments
{
	public static IMcpServerBuilder WithAbsolutePathArguments(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
			PathArguments.Relative(context.Params?.Arguments) is { } refusal
				? throw new McpException(refusal)
				: next(context, cancellationToken)));
}
