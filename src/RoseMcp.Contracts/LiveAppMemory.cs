namespace RoseMcp.Contracts;

/// <summary>
/// One debug session's memory: its live-app host, which is Rose's cost, and the app it debugs, which is not, kept
/// apart so a window does not count an app's memory as Rose's.
/// </summary>
public sealed record LiveAppMemory
{
	public required string SessionId { get; init; }

	/// <summary>The app being debugged, as the session describes it.</summary>
	public required string TargetDescription { get; init; }

	/// <summary>The live-app host, while the session has one: null once it has ended, or where it could not be read.</summary>
	public ProcessMemory? Host { get; init; }

	/// <summary>
	/// The app, while the host is attached to it and serving: null once it has exited or the session has ended,
	/// since Windows hands an exited process's id to the next one it starts, and where it could not be read.
	/// </summary>
	public ProcessMemory? Target { get; init; }
}
