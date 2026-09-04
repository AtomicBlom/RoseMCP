namespace RoseMcp.Worker;

/// <summary>
/// The three granularities a change actually arrives at.
/// <para>
/// Three rather than one because all three came up constantly, and rather than more because they
/// were enough: the observed unit of change was a whole member or its body, essentially every time.
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
}
