using Microsoft.Extensions.Options;

namespace RoseMcp.Broker;

/// <summary>
/// Turns the path arguments of one call into <see cref="RootedPath"/>s, against the directory that
/// call came from.
/// <para>
/// One place decides what "where the caller is standing" means, for the same reason one place
/// decides which workspace a call meant: the two answers have to agree, and a tool that worked it
/// out for itself would be the one that disagreed. A relayed session says where it is in
/// <see cref="CallOrigin"/>; a session with no relay in front of it is answered by
/// <see cref="BrokerOptions.DefaultWorkspaceRoot"/>, which is that process's own working directory
/// and so is the same fact arrived at differently.
/// </para>
/// </summary>
public sealed class CallerPaths(IOptions<BrokerOptions> options)
{
	private readonly BrokerOptions _options = options.Value;

	/// <summary>The directory this call's relative paths are measured from.</summary>
	public string Origin => CallOrigin.Directory ?? _options.DefaultWorkspaceRoot;

	/// <summary>One path argument, absolute.</summary>
	public RootedPath? Of(string? raw) => RootedPath.From(raw, Origin);

	/// <summary>A list argument, absolute, with the nulls kept so positions still line up.</summary>
	public RootedPath?[] Each(IEnumerable<string?>? raw) => raw is null ? [] : [.. raw.Select(Of)];
}
