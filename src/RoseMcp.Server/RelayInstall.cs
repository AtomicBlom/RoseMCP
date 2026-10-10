using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// Where an installed relay finds the install it belongs to.
/// <para>
/// The relay sits in a folder of its own under the install root, which a deploy leaves alone so the
/// relay keeps running across it. That makes its own assemblies as old as the last change to the
/// relay protocol, which is fine for forwarding and wrong for anything else: with no tray to relay
/// to, a relay that hosted the broker itself would serve every tool from that old build, and its
/// workers would hold the install's files through the next deploy. So it hands the session to the
/// install root's broker instead, started as a child, which is always the build the last deploy put
/// down.
/// </para>
/// </summary>
public static class RelayInstall
{
	/// <summary>The server's executable name, as the published layout names it.</summary>
	private const string ServerExecutable = "RoseMcp.Server";

	/// <summary>
	/// The install root's broker, for a relay running from <paramref name="baseDirectory"/>: the server
	/// one level above the relay's folder. Null when the directory is not an install's relay folder --
	/// a build output, the install root itself, a Linux install -- where the process hosts its broker
	/// as it always has.
	/// </summary>
	public static string? BrokerFor(string baseDirectory)
	{
		var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));

		var isRelayFolder = string.Equals(Path.GetFileName(directory), RelayProtocol.Folder, StringComparison.OrdinalIgnoreCase);
		if (!isRelayFolder || Path.GetDirectoryName(directory) is not { } root) return null;

		var broker = Path.Combine(root, OperatingSystem.IsWindows() ? ServerExecutable + ".exe" : ServerExecutable);

		return File.Exists(broker) ? broker : null;
	}
}
