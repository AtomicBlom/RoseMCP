namespace RoseMcp.Contracts;

/// <summary>Where in a body to put code that is being inserted rather than replacing it.</summary>
public enum BodyPosition
{
	/// <summary>At the top of the block, before everything already there.</summary>
	Start,

	/// <summary>
	/// At the end of the block -- meaning before a closing return, throw, break, continue or goto,
	/// since anything after one of those is unreachable and CS0162.
	/// </summary>
	End,
}
