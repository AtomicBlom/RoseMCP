namespace RoseMcp.Contracts;

/// <summary>
/// Whether one project's build output is newer than the sources it was built from.
/// <para>
/// The question is not "does it compile" -- a build can be green while the thing about to be
/// executed is last week's. Three separate bugs in one session came from taking an artefact's
/// existence for its currency, and in two of them the solution built perfectly: a test ran
/// yesterday's debug host and reported a failure describing a rename that had already been done.
/// </para>
/// </summary>
public sealed record ProjectFreshness
{
	public required string Project { get; init; }

	/// <summary>The assembly the design-time build says this project produces.</summary>
	public string? OutputPath { get; init; }

	/// <summary>When that file was last written, or null when it is not there at all.</summary>
	public DateTime? OutputWrittenUtc { get; init; }

	/// <summary>The most recently written source file, which is the one that makes it stale.</summary>
	public string? NewestSourcePath { get; init; }

	public DateTime? NewestSourceWrittenUtc { get; init; }

	/// <summary>
	/// How many sources are newer than the output. One is enough to be stale; the count is worth
	/// having because a large one usually means a branch changed under the build rather than an edit.
	/// </summary>
	public required int SourcesNewerThanOutput { get; init; }

	public required bool Stale { get; init; }

	/// <summary>The answer in words, since the interesting cases are not all "yes" or "no".</summary>
	public required string Verdict { get; init; }
}
