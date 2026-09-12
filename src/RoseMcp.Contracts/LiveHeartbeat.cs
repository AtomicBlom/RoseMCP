namespace RoseMcp.Contracts;

/// <summary>
/// When a target last produced a debug event, and what it was. The one signal that separates a target
/// which has stopped executing from one that is merely quiet about diagnostics.
/// <para>
/// A process tells you nothing here. The way a UWP app stops is a job-object freeze: the system stops
/// every thread in the group without marking any of them suspended, so CPU time, thread state and
/// window responsiveness read the same for a frozen app and a wedged one. What a target that is not
/// executing does do is stop producing events, so the age of this is the discriminator -- and it is
/// why the age reaches a caller rather than only a log.
/// </para>
/// <para>
/// The age is deliberately not turned into a verdict here. Which ages are suspicious depends on what
/// the target does when idle: a probe on a timer is silent for milliseconds, a real app for minutes.
/// A threshold chosen in this record would be a guess wearing a diagnosis.
/// </para>
/// </summary>
public sealed record LiveHeartbeat
{
	public required long Sequence { get; init; }

	public required DateTime TimestampUtc { get; init; }

	public required LiveDebugEventKind Kind { get; init; }
}
