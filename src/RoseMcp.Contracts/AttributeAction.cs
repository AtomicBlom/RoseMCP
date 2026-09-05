namespace RoseMcp.Contracts;

/// <summary>What to do with an attribute of the name the caller wrote.</summary>
public enum AttributeAction
{
	/// <summary>
	/// Replace the one attribute of that name, add it where there is none, and refuse where there
	/// are several. Refusing matters: a theory with four <c>InlineData</c> attributes is ordinary,
	/// and replacing the first of them would compile and change the wrong case.
	/// </summary>
	Set,

	/// <summary>Add another, whatever is already there.</summary>
	Add,

	/// <summary>Take one away, refusing where there are several of that name or none at all.</summary>
	Remove,
}
