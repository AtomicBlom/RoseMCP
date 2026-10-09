namespace RoseMcp.Contracts;

/// <summary>
/// A result from an operation that writes, or would write, source files.
/// <para>
/// Separate from a plain <see cref="WorkspaceScopedResult"/> because a change has a consequence a
/// read does not: the files it touched may belong to projects that other solutions in the same
/// tree also build, and the operation only ever saw one solution. Naming what was written is what
/// lets the broker work that out without every tool having to.
/// </para>
/// <para>
/// What every write has is here, so the broker can shape all of them in one place: the paths named
/// relative to the calling session's directory, the diff left off an applied write unless it was asked
/// for, and long lists cut with a count. A tool added later is shaped without knowing it is.
/// </para>
/// </summary>
public abstract record WorkspaceMutationResult : WorkspaceScopedResult
{
	/// <summary>False when this was a preview, or when there was nothing to write; nothing was written.</summary>
	public required bool Applied { get; init; }

	/// <summary>
	/// Every file this wrote, or would write when it was only a preview, each named once with where
	/// it changed. A result that is about one file names it here and nowhere else, as the first entry.
	/// </summary>
	public IReadOnlyList<ChangedFile> ChangedFiles { get; init; } = [];

	/// <summary>
	/// The unified diff. A preview carries it, because a preview is the diff; an applied write carries
	/// it only when <c>includeDiff</c> asked, since what it shows is mostly what the caller sent, and
	/// <see cref="ChangedFiles"/> already says where it landed. Absent past the length a result carries,
	/// with a notice.
	/// </summary>
	public string? Diff { get; init; }

	/// <summary>
	/// Caveats worth reading before trusting the change: markup the compiler cannot see, a sibling
	/// solution that shares these files, anything the operation deliberately left alone. Each is said
	/// only where it is true of this call; what is true of every call is in the tool's description.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
