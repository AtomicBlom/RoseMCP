using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp;

/// <summary>
/// Refuses a path argument that is not absolute. <see cref="PathArguments"/> holds the rule and the
/// reason; this is the host's end of it.
/// <para>
/// A relative path resolves here against this process's working directory, which the caller has no
/// reason to know and which describes nothing about the app being debugged. Two checkouts of one
/// repository hold the same markup under the same relative path, so the wrong one reads as a
/// working apply: the live edit lands, and it is an edit to a file nobody is looking at.
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
