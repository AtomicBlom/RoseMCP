using System.Reflection;

namespace RoseMcp.Contracts;

/// <summary>
/// The version a host tells its client during <c>initialize</c>, read off the assembly asking.
/// <para>
/// MinVer stamps it from the git tag at build time, so a version in a bug report names a commit.
/// A hard-coded one names nothing: three of the four hosts reported <c>0.1.0</c> for every build
/// ever made, which is worse than no version at all, because it looks like an answer.
/// </para>
/// <para>
/// Each host asks about its own assembly rather than a shared one. They are published separately --
/// a stdio server from an install, a worker beside it, a live-app host under its own runtime folder
/// -- so an install that half updated is a real state, and the point of the number is to say which
/// binary is speaking.
/// </para>
/// </summary>
public static class HostVersion
{
	/// <summary>
	/// The informational version of <paramref name="assembly"/>, without the build metadata after
	/// '+' -- that is the commit hash, which belongs in a log rather than in a handshake.
	/// <para>
	/// <c>0.0.0</c> when the attribute is missing, which is honest about not knowing rather than
	/// inventing a number. A build from an archive with no <c>.git</c> reports MinVer's own
	/// <c>0.0.0-alpha.0</c> for the same reason.
	/// </para>
	/// </summary>
	public static string Of(Assembly assembly) =>
		assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion.Split('+')[0]
		?? "0.0.0";
}
