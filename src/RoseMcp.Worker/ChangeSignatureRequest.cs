namespace RoseMcp.Worker;

/// <summary>Which member to reshape, and what its parameters should be.</summary>
public sealed record ChangeSignatureRequest
{
	/// <summary>
	/// The member, as Namespace.Type.Member, with a parameter list to pick an overload -- which
	/// matters more here than anywhere else, since the point of the call is that there will be a
	/// different overload afterwards.
	/// </summary>
	public required string Symbol { get; init; }

	/// <summary>
	/// The parameters the member should have, written as they would be between the parentheses:
	/// <c>int count, string name, bool loud = false</c>. Declarative rather than a list of
	/// operations, because it is the thing the caller actually knows -- what the signature should
	/// say -- and what changed can be worked out from it.
	/// </summary>
	public required string Parameters { get; init; }

	/// <summary>
	/// What to pass at existing call sites for a new parameter that has no default, as
	/// <c>name=expression</c>. A new parameter with a default needs none: every call site can go on
	/// saying nothing about it, which is the whole reason to give one.
	/// </summary>
	public IReadOnlyList<string> Arguments { get; init; } = [];

	/// <summary>Which file, when the member is declared in more than one -- a partial.</summary>
	public string? FilePath { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Compile the solution afterwards and report what the change broke. On by default, and the
	/// whole solution rather than the projects nearby: a call site this missed is by definition
	/// somewhere it did not look.
	/// </summary>
	public bool Verify { get; init; } = true;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
