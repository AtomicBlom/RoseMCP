using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core;

/// <summary>
/// The command a person runs to register RoseMCP with Claude Code, as the tray shows it.
/// <para>
/// The relay form wherever an install has a relay: a session connected through the relay keeps its
/// connection across a deploy, hears when the tools change, and tells the broker which directory it
/// runs in, and a client registered with the broker's http endpoint gets none of that. The http form
/// only where there is no relay to name -- a tray run out of a build output -- since a command naming
/// a file that is not there registers nothing that works.
/// </para>
/// </summary>
public static class RelayRegistration
{
	/// <summary>The port a relay probes for a tray when it is given none.</summary>
	public const int DefaultPort = 5077;

	/// <summary>The address a relay probes when it is given none.</summary>
	public const string DefaultHost = "127.0.0.1";

	/// <summary>The relay's executable, as the published layout names it, on Windows.</summary>
	private const string RelayExecutable = "RoseMcp.Server.exe";

	/// <summary>
	/// The relay installed beside a tray running from <paramref name="trayDirectory"/>: in the relay
	/// folder under the install root the tray's own folder sits in. Null where there is none.
	/// </summary>
	public static string? RelayFor(string trayDirectory)
	{
		var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(trayDirectory)));
		if (root is null) return null;

		var relay = Path.Combine(root, RelayProtocol.Folder, RelayExecutable);

		return File.Exists(relay) ? relay : null;
	}

	/// <summary>
	/// The registration command for a broker at <paramref name="host"/>:<paramref name="port"/>,
	/// through <paramref name="relay"/> where there is one. User scope, because a relay finds the
	/// solution from the directory a session starts in, so one registration serves every repository.
	/// The address is passed on only where it is not the one a relay looks at anyway.
	/// </summary>
	public static string Command(string? relay, string host, int port)
	{
		if (relay is null) return $"claude mcp add --transport http rose http://{host}:{port}";

		var command = $"claude mcp add rose -s user -- \"{relay}\"";
		if (!string.Equals(host, DefaultHost, StringComparison.Ordinal)) command += $" --host {host}";
		if (port != DefaultPort) command += $" --port {port}";

		return command;
	}
}
