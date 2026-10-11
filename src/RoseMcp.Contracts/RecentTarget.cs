namespace RoseMcp.Contracts;

/// <summary>
/// Something a person debugged from a window, kept so they can do it again: a program with its arguments, a packaged
/// app, or the executable of a process they attached to.
/// </summary>
public sealed record RecentTarget
{
	/// <summary>How the session got hold of its target.</summary>
	public required LiveAppTargetKind Kind { get; init; }

	/// <summary>What a window calls it.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The program launched, or the executable of the process attached to.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>The arguments a program was launched with.</summary>
	public string? Arguments { get; init; }

	/// <summary>The packaged app launched.</summary>
	public string? AppUserModelId { get; init; }

	/// <summary>When it was last debugged.</summary>
	public required DateTime LastUsedUtc { get; init; }
}
