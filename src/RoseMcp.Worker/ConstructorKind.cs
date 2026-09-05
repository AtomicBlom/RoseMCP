namespace RoseMcp.Worker;

/// <summary>
/// Which constructor an address names, if it names one at all.
/// <para>
/// A constructor is the one member whose source spelling and whose symbol name are different
/// things: it is declared with the name of its type and the compiler calls it <c>.ctor</c>. Both
/// spellings reach it, and neither can be confused with anything else -- C# forbids a member
/// sharing the name of its enclosing type, so <c>Type.Type</c> can only be a constructor.
/// </para>
/// </summary>
public enum ConstructorKind
{
	/// <summary>The address names an ordinary member or type.</summary>
	None,

	/// <summary><c>Type.Type</c> or <c>Type..ctor</c>.</summary>
	Instance,

	/// <summary><c>Type..cctor</c>, the static constructor.</summary>
	Static,
}
