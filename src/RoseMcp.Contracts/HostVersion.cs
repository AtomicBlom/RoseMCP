using System.Reflection;

namespace RoseMcp.Contracts;

/// <summary>
/// The release version an assembly belongs to, read off the assembly asking, which is what an
/// update check compares against a release tag.
/// <para>
/// MinVer stamps it from the git tag at build time. A hard-coded one names nothing: a host reporting
/// <c>0.1.0</c> for every build ever made is worse than no version at all, because it looks like an
/// answer.
/// </para>
/// <para>
/// A version is not a build. Two local builds at the same height above a tag share one, so what a
/// handshake sends and compares is <see cref="BuildIdentity"/>, which carries the commit as well.
/// </para>
/// </summary>
public static class HostVersion
{
	/// <summary>
	/// The informational version of <paramref name="assembly"/>, without the build metadata after
	/// '+', which is the commit and belongs to <see cref="BuildIdentity"/> rather than to a version a
	/// release is compared by.
	/// <para>
	/// <c>0.0.0</c> when the attribute is missing, which is honest about not knowing rather than
	/// inventing a number. A build from an archive with no <c>.git</c> reports MinVer's own
	/// <c>0.0.0-alpha.0</c> for the same reason.
	/// </para>
	/// </summary>
	public static string Of(Assembly assembly) => BuildIdentity.Of(assembly).Version;
}
