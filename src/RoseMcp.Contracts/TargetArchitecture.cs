namespace RoseMcp.Contracts;

/// <summary>
/// The processor architecture a target process runs as. A live-app session's host must run as the
/// same architecture, because it loads the target's own debugging and diagnostics DLLs into itself.
/// On a Windows-on-ARM machine this matters: classic UWP has no ARM64 runtime and runs x64 under
/// emulation, whereas modern .NET runs natively as ARM64.
/// </summary>
public enum TargetArchitecture
{
	/// <summary>Could not be determined.</summary>
	Unknown,

	X86,

	X64,

	Arm64,
}
