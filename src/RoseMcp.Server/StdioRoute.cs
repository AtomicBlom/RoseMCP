namespace RoseMcp.Server;

/// <summary>One way a stdio session can be served, tried in the order <see cref="RelayInstall.RoutesFor"/> gives.</summary>
public enum StdioRoute
{
	/// <summary>Relay to the tray, which shares one warm worker per solution with every session.</summary>
	Tray,

	/// <summary>Start the install root's broker as a child and relay to it.</summary>
	ChildBroker,

	/// <summary>Host the broker in this process, as a build output or a Linux install does.</summary>
	HostHere,

	/// <summary>End the session, saying why on stderr, rather than serve it from the wrong build.</summary>
	Refuse,
}
