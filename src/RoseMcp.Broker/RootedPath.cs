namespace RoseMcp.Broker;

/// <summary>
/// A path argument, fully qualified, and the only kind the routing layer and the workers accept.
/// <para>
/// A relative path is a fact about where the caller is standing, and nothing else. Resolved
/// anywhere else it answers a different call: the broker's own working directory is the tray's
/// install directory in http mode, and in stdio mode it is whichever checkout the process was
/// started in. Six worktrees of one repository is the ordinary case here and each holds the same
/// relative paths, so <c>tests/Foo.cs</c> resolved against the wrong one names a real file, loads a
/// real solution, and is written to and reported as a success -- a wrong side effect the caller's
/// own <c>git status</c> cannot see.
/// </para>
/// <para>
/// So there is no way to make one of these without saying what the path is measured from. That is
/// the whole of the type: <see cref="From"/> takes the base, <see cref="Absolute"/> is for a path
/// that is already qualified and refuses one that is not, and whatever comes out is qualified. A
/// caller's absolute path into another checkout is still honoured, because they said it -- the
/// failure here was inferring one, never accepting one.
/// </para>
/// </summary>
public sealed class RootedPath
{
	private RootedPath(string value) => Value = value;

	/// <summary>The path, absolute and normalised.</summary>
	public string Value { get; }

	public override string ToString() => Value;

	/// <summary>
	/// A path as the caller wrote it, measured from where they were standing. Null in is null out,
	/// because an argument nobody supplied is not a path that failed to resolve.
	/// </summary>
	/// <param name="raw">The argument as it arrived, absolute or relative.</param>
	/// <param name="origin">The directory a relative one is measured from. Must itself be absolute.</param>
	public static RootedPath? From(string? raw, string origin)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;

		if (!Path.IsPathFullyQualified(origin))
		{
			throw new ArgumentException(
				$"A relative path can only be measured from an absolute directory, and '{origin}' is not one.",
				nameof(origin));
		}

		// Handles both: an already-qualified path comes back normalised and the base is ignored.
		return new RootedPath(Path.GetFullPath(raw, origin));
	}

	/// <summary>
	/// One that is absolute already -- a solution path off the worker list, a fixture's path in a
	/// test. It refuses a relative path rather than picking a base for it, so this is a shorter
	/// spelling of <see cref="From"/> and not a way around it.
	/// </summary>
	public static RootedPath Absolute(string path)
	{
		if (!Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException(
				$"'{path}' is relative, and nothing here knows what to measure it from.", nameof(path));
		}

		return new RootedPath(Path.GetFullPath(path));
	}
}
