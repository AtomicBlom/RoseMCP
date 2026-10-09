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
/// <para>
/// A <c>workspaceKey</c> changes which worker answers and not where a relative path is measured from.
/// A result names its files relative to this same directory (<see cref="ResultPaths"/> for a read,
/// <see cref="WritePaths"/> for a write), so a path it returned means one file however it comes back
/// -- with the key, without it, or through the caller's own file tools.
/// </para>
/// </summary>
public sealed class CallerPaths(IOptions<BrokerOptions> options)
{
	private readonly BrokerOptions _options = options.Value;

	/// <summary>The directory this call's relative paths are measured from.</summary>
	public string Origin => CallOrigin.Directory ?? _options.DefaultWorkspaceRoot;

	/// <summary>
	/// The directory the caller is known to stand in, or null where it is not known: a relayed session
	/// says, a stdio broker's own directory is its client's, and an http session with no relay in front
	/// of it says nothing. Every result is made relative only to this -- a read's references
	/// (<see cref="ResultPaths"/>) and a write's files (<see cref="WritePaths"/>) alike -- since a path
	/// relative to a directory the caller is not in is a path that names nothing when it is sent back.
	/// </summary>
	public string? KnownOrigin => CallOrigin.Directory ?? (_options.DefaultRootIsTheCaller ? _options.DefaultWorkspaceRoot : null);

	/// <summary>One path argument, absolute.</summary>
	public RootedPath? Of(string? raw) => RootedPath.From(raw, Origin);

	/// <summary>A list argument, absolute, with the nulls kept so positions still line up.</summary>
	public RootedPath?[] Each(IEnumerable<string?>? raw) => raw is null ? [] : [.. raw.Select(Of)];
}
