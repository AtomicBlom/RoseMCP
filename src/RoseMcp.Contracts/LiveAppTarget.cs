namespace RoseMcp.Contracts;

/// <summary>
/// What a live-app session is aimed at. One session owns exactly one target. The fields that matter
/// depend on <see cref="Kind"/>; the rest are null.
/// </summary>
public sealed record LiveAppTarget
{
	public required LiveAppTargetKind Kind { get; init; }

	/// <summary>The process to attach to, for <see cref="LiveAppTargetKind.AttachProcess"/>.</summary>
	public int? ProcessId { get; init; }

	/// <summary>The executable to launch, for <see cref="LiveAppTargetKind.LaunchExecutable"/>.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>Command-line arguments for a launched executable.</summary>
	public string? Arguments { get; init; }

	/// <summary>The application user-model id to activate, for <see cref="LiveAppTargetKind.LaunchUwp"/>.</summary>
	public string? AppUserModelId { get; init; }

	/// <summary>A short human description for logs and the admin view.</summary>
	public string? Description { get; init; }
}
