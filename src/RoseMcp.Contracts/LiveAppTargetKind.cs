namespace RoseMcp.Contracts;

/// <summary>How a live-app session gets hold of its target process.</summary>
public enum LiveAppTargetKind
{
	/// <summary>Attach to a process that is already running.</summary>
	AttachProcess,

	/// <summary>Launch an executable and own it.</summary>
	LaunchExecutable,

	/// <summary>Activate a packaged (UWP) app and own it.</summary>
	LaunchUwp,
}
