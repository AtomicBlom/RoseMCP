namespace RoseMcp.Worker;

/// <summary>Which declaration to write, and what to write into it.</summary>
public sealed record MemberEditRequest
{
	public required MemberEditKind Kind { get; init; }

	/// <summary>
	/// The member to write over, or the type to add to. Named rather than pointed at: a line and
	/// column has to be found by reading the file, and is wrong the moment an earlier edit lands.
	/// </summary>
	public required string Symbol { get; init; }

	/// <summary>
	/// The C# to write. A whole declaration for <see cref="MemberEditKind.Replace"/> and
	/// <see cref="MemberEditKind.Add"/>; statements, a block, or <c>=&gt; expression;</c> for
	/// <see cref="MemberEditKind.ReplaceBody"/>.
	/// </summary>
	public required string Code { get; init; }

	/// <summary>
	/// Which file, when the name alone does not settle it -- a partial type, or a partial member.
	/// Also the workspace hint the broker ranks, being the one argument here that names a path.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>
	/// Put the new member after this one, by name. Adding only. Placement is worth controlling
	/// because a member's neighbours are how a reader finds it, and appending to the end of a
	/// several-hundred-line type puts a private helper below the public surface it serves.
	/// </summary>
	public string? After { get; init; }

	/// <summary>Put the new member before this one, by name. Adding only.</summary>
	public string? Before { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Compile the projects holding the file afterwards and report what the edit broke. On by
	/// default: it is the whole reason this is one call rather than two, and it costs a warm
	/// compilation rather than a build.
	/// </summary>
	public bool Verify { get; init; } = true;

	/// <summary>
	/// Fail rather than apply if the workspace has moved past this revision. Matters when more than
	/// one client shares a broker in http mode.
	/// </summary>
	public long? ExpectedRevision { get; init; }
}
