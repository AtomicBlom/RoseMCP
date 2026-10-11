namespace RoseMcp.Contracts;

/// <summary>
/// The entries of the activity record a query matched, as <c>GET /operator/activity</c> answers: the newest
/// first, up to the limit asked for, and where the next page starts.
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

	/// <summary>
	/// How many entries matched from where this page starts, of which <see cref="Entries"/> holds the newest; more than
	/// its count where the limit cut it, and then <see cref="Next"/> reads on.
	/// </summary>
	public required int Matched { get; init; }

	/// <summary>
	/// Where the next page starts, passed back as <c>before</c> with the same filters; null where this page holds the
	/// last of what matched. A cursor rather than an offset, so calls arriving between two reads do not shift the
	/// second page onto entries the first already showed.
	/// </summary>
	public string? Next { get; init; }

	/// <summary>
	/// What the page held when it was read, as an opaque string: passed back as <c>after</c> with a <c>waitSeconds</c>
	/// and the same filters, the next read waits until the page holds something else -- a call starting or ending.
	/// </summary>
	public string? Version { get; init; }

	/// <summary>
	/// Whether the record holds entries older than a listing reaches. A listing reads at most a set number of the
	/// newest entries, so on a busy machine the oldest days are on disk but not listed; each can still be read by its id.
	/// </summary>
	public bool OlderNotListed { get; init; }

	/// <summary>Where the record is kept, so a person can find the files.</summary>
	public string? Directory { get; init; }

	/// <summary>How long an entry is kept before it is deleted.</summary>
	public required TimeSpan Retention { get; init; }
}
