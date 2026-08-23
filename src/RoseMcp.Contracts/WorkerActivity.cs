namespace RoseMcp.Contracts;

/// <summary>
/// One operation a worker is running, or has just finished.
/// <para>
/// Worth reporting because a warm Roslyn host looks identical from the outside whether it is idle
/// or eight seconds into a design-time build. Without this the only visible symptom of a slow load
/// is an agent that appears to have hung, and no way to tell which of several solutions is to blame.
/// </para>
/// </summary>
public sealed record WorkerActivity
{
	/// <summary>Stable for the life of the operation, so a UI can update a row rather than replace it.</summary>
	public required long Id { get; init; }

	/// <summary>
	/// The tool being served, or a lifecycle step such as starting a worker or loading its solution.
	/// Tool names are reported verbatim, so what the tray shows is what the client asked for.
	/// </summary>
	public required string Operation { get; init; }

	/// <summary>What the operation is aimed at -- a file, project, or symbol. Null means the whole solution.</summary>
	public string? Target { get; init; }

	public required DateTime StartedUtc { get; init; }

	/// <summary>Time so far for a running operation; total time for a finished one.</summary>
	public required TimeSpan Elapsed { get; init; }

	public required ActivityOutcome Outcome { get; init; }

	/// <summary>The worker's last word on what it is doing. Null until it reports something.</summary>
	public string? Message { get; init; }

	/// <summary>
	/// How far along, 0 to 100, or null when the operation genuinely cannot say -- finding
	/// references has no idea up front how much of the solution it will have to look at. Null is
	/// reported as null rather than as zero, because a bar stuck at zero reads as a hang.
	/// </summary>
	public double? PercentComplete { get; init; }

	/// <summary>Present only on <see cref="ActivityOutcome.Failed"/>.</summary>
	public string? Error { get; init; }
}
