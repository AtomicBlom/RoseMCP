using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Which member moves where, and what happens to the code that calls it.</summary>
public sealed record MoveMemberRequest
{
	/// <summary>The member to move, as Namespace.Type.Member, with a parameter list for an overload.</summary>
	public required string Symbol { get; init; }

	/// <summary>The type it moves into, as Namespace.Type.</summary>
	public required string TargetType { get; init; }

	/// <summary>
	/// What to do about the call sites: qualify each one with the new type, or add a
	/// <c>using static</c> to each file that calls it and leave the calls alone.
	/// <para>
	/// The decision a person doing this by hand forgets to make consistently, and the reason this is
	/// one tool rather than an add followed by a delete.
	/// </para>
	/// </summary>
	public CallSiteStyle CallSites { get; init; } = CallSiteStyle.Qualify;

	/// <summary>Which file, when the member is declared in more than one -- a partial type.</summary>
	public string? FilePath { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>Compile afterwards and report what the move broke.</summary>
	public bool Verify { get; init; } = true;

	/// <summary>How much to compile. See <see cref="VerifyScope"/>.</summary>
	public VerifyScope VerifyScope { get; init; } = VerifyScope.Auto;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
