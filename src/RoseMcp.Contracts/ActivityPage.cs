namespace RoseMcp.Contracts;

/// <summary>
/// The entries of the activity record a query matched, as <c>GET /operator/activity</c> answers: the newest
/// first, up to the limit asked for.
/// </summary>
public sealed record ActivityPage
{
	/// <summary>The entries, newest first, the calls still running among them.</summary>
	public IReadOnlyList<ActivityEntry> Entries { get; init; } = [];

	/// <summary>
	/// Whether each entry carries its arguments, response and diffs. A listing leaves them out unless asked,
	/// since they are most of an entry's size; an entry read by its id always has them.
	/// </summary>
	public required bool Bodies { get; init; }

	/// <summary>How many entries matched, of which <see cref="Entries"/> holds the newest; more than its count where the limit cut it.</summary>
	public required int Matched { get; init; }

	/// <summary>Where the record is kept, so a person can find the files.</summary>
	public string? Directory { get; init; }

	/// <summary>How long an entry is kept before it is deleted.</summary>
	public required TimeSpan Retention { get; init; }
}
