using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The running .NET processes a person could attach to, for the start page of a window. An interface so the operator
/// routes can be driven by a test with processes of its own, and so a host that is not on Windows lists none.
/// </summary>
public interface IRunningApps
{
	/// <summary>
	/// The .NET processes this user runs that have a window on the desktop, or with <paramref name="every"/> every
	/// .NET process this user runs. No session is named on any of them: which sessions are debugging what is the
	/// session manager's to say. Never throws; a process that cannot be read is left out.
	/// </summary>
	IReadOnlyList<RunningApp> Read(bool every);
}

/// <summary>The packaged apps installed for this user that a person could launch under the debugger.</summary>
public interface IInstalledApps
{
	/// <summary>Every app of every package installed for this user but frameworks and resources. Never throws.</summary>
	IReadOnlyList<InstalledApp> Read();
}
