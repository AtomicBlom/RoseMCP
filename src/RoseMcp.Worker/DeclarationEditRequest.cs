using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// A change to what surrounds a declaration rather than to the code inside it: its documentation
/// comment, or one of its attributes.
/// <para>
/// Separate from <see cref="MemberEditRequest"/> because the payload is a different kind of thing.
/// A member edit carries C#, parsed against a synthetic container; this carries prose or one
/// attribute, and what makes each of them wrong is different -- malformed XML on one side, an
/// ambiguous attribute name on the other.
/// </para>
/// </summary>
public sealed record DeclarationEditRequest
{
	/// <summary>The member or type, as Namespace.Type.Member, with a parameter list for an overload.</summary>
	public required string Symbol { get; init; }

	/// <summary>
	/// The documentation comment as XML, or as plain text taken to be the summary. Set for a
	/// comment edit and ignored otherwise.
	/// </summary>
	public string? Comment { get; init; }

	/// <summary>
	/// The attribute as it is written in source, brackets optional. Set for an attribute edit.
	/// </summary>
	public string? Attribute { get; init; }

	/// <summary>What to do with the attribute.</summary>
	public AttributeAction Action { get; init; }

	/// <summary>Which file, when the name is declared in more than one -- a partial type or member.</summary>
	public string? FilePath { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>Compile afterwards and report what the edit broke.</summary>
	public bool Verify { get; init; } = true;

	/// <summary>How much to compile. See <see cref="VerifyScope"/>.</summary>
	public VerifyScope VerifyScope { get; init; } = VerifyScope.Auto;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
