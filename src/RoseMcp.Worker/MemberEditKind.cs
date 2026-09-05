namespace RoseMcp.Worker;

/// <summary>
/// The four granularities a change actually arrives at.
/// <para>
/// Four rather than one because all of them came up constantly, and rather than more because they
/// were enough: the observed unit of change was a whole member, its body, or its existence,
/// essentially every time.
/// Statement-level editing is deliberately absent -- it is the granularity that sounds most useful
/// and was almost never the thing being changed.
/// </para>
/// </summary>
public enum MemberEditKind
{
	/// <summary>Write over a whole declaration, signature included.</summary>
	Replace,

	/// <summary>Write over a body, leaving the signature exactly as it was found.</summary>
	ReplaceBody,

	/// <summary>Add one or more members to a type.</summary>
	Add,

	/// <summary>
	/// Take a member out, with its documentation comment and its attributes.
	/// <para>
	/// The one edit that is only safe semantically: whether anything still calls it, whether it is
	/// the last of a partial, and whether an abstract base is left unimplemented are all questions a
	/// text edit cannot ask.
	/// </para>
	/// </summary>
	Delete,
}
