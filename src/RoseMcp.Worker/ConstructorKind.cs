namespace RoseMcp.Worker;

/// <summary>
/// Which constructor an address names, if it names one at all.
/// <para>
/// A constructor is the one member whose source spelling and whose symbol name are different
/// things: it is declared with the name of its type and the compiler calls it <c>.ctor</c>. Both
/// spellings reach it. <c>Type..ctor</c> can be nothing else; <c>Type.Type</c> can also be a type
/// named for the namespace it is in, which <see cref="SymbolAddress.AsType"/> keeps as a second
/// reading for the compilation to settle.
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
