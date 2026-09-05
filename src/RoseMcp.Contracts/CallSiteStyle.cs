namespace RoseMcp.Contracts;

/// <summary>
/// What happens to the code that calls a member being moved to another type.
/// <para>
/// The choice a person doing this by hand makes inconsistently, because it is made once per file
/// and there is nothing to remind them what they decided in the last one.
/// </para>
/// </summary>
public enum CallSiteStyle
{
	/// <summary>
	/// Write the new type in front of every call. Explicit, and it leaves the calling file saying
	/// where the member lives now.
	/// </summary>
	Qualify,

	/// <summary>
	/// Add a using static for the new type to each file that calls it, and leave the calls as they
	/// are. Smaller diff, at the cost of a file whose calls no longer say where they go.
	/// </summary>
	UsingStatic,
}
