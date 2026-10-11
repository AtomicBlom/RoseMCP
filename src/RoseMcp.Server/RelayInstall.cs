using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// Where an installed relay finds the install it belongs to, and how a stdio session is served.
/// <para>
/// The relay sits in a folder of its own under the install root, which a deploy leaves alone so the
/// relay keeps running across it. That makes its own assemblies as old as the last change to the
/// relay protocol, which is fine for forwarding and wrong for anything else: a relay that hosted the
/// broker itself would serve every tool from that old build, its workers would hold the install's
/// files, and a deploy -- which keeps the relay's processes running -- would wait on those workers and
/// abort. So an installed relay never hosts: with no tray to relay to it hands the session to the
/// install root's broker, started as a child, which is always the build the last deploy put down, and
/// where that will not start either it ends the session and says why.
/// </para>
/// </summary>
public static class RelayInstall
{
	/// <summary>The server's executable name, as the published layout names it.</summary>
	private const string ServerExecutable = "RoseMcp.Server";

	/// <summary>
	/// The install root's broker, for a relay running from <paramref name="baseDirectory"/>: the server
	/// one level above the relay's folder, whether or not it is there now -- a root without one is an
	/// install mid-deploy or damaged, and still not a place to host from. Null when the directory is not
	/// an install's relay folder -- a build output, the install root itself, a Linux install -- where
	/// the process hosts its broker as it always has.
	/// </summary>
	public static string? BrokerFor(string baseDirectory)
	{
		var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));

		var isRelayFolder = string.Equals(Path.GetFileName(directory), RelayProtocol.Folder, StringComparison.OrdinalIgnoreCase);
		if (!isRelayFolder || Path.GetDirectoryName(directory) is not { } root) return null;

		return Path.Combine(root, OperatingSystem.IsWindows() ? ServerExecutable + ".exe" : ServerExecutable);
	}

	/// <summary>
	/// The ways to serve a stdio session, in the order to try them, each tried only where the one
	/// before it could not connect.
	/// <para>
	/// A tray that is listening comes first. After it, an installed relay tries the install's broker
	/// and then refuses, never hosting -- a tray that answers the probe and then refuses the session is
	/// one way to get there, and a deploy cannot finish while a relay holds workers. Anything else hosts
	/// the broker itself.
	/// </para>
	/// </summary>
	/// <param name="noRelay">The process was told to host, as a relay's child broker is.</param>
	/// <param name="trayListening">Whether a tray answered the probe.</param>
	/// <param name="installed">Whether this process runs from an install's relay folder: <see cref="BrokerFor"/> named a broker.</param>
	public static IReadOnlyList<StdioRoute> RoutesFor(bool noRelay, bool trayListening, bool installed)
	{
		if (noRelay) return [StdioRoute.HostHere];

		if (installed)
		{
			return trayListening
				? [StdioRoute.Tray, StdioRoute.ChildBroker, StdioRoute.Refuse]
				: [StdioRoute.ChildBroker, StdioRoute.Refuse];
		}

		return trayListening ? [StdioRoute.Tray, StdioRoute.HostHere] : [StdioRoute.HostHere];
	}
}
