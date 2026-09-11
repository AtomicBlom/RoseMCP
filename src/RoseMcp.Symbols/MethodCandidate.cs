namespace RoseMcp.Symbols;

/// <summary>One method a search turned up, with everything needed to break in it or to show it.</summary>
public sealed record MethodCandidate
{
	/// <summary>
	/// The spelling a breakpoint is set with: <c>Assembly!Namespace.Type.Method</c>.
	/// <para>
	/// Always assembly-qualified, even where the type name would have implied the right module. The
	/// implied form guesses the assembly from the first namespace segment, and something picked out
	/// of a list of real methods should not be able to fail to bind over a guess.
	/// </para>
	/// </summary>
	public required string Location { get; init; }

	/// <summary>The assembly's simple name.</summary>
	public required string Module { get; init; }

	/// <summary>The module file the match was read from, for reading its source next.</summary>
	public required string ModulePath { get; init; }

	/// <summary>The declaring type as metadata spells it, nesting and arity included.</summary>
	public required string TypeName { get; init; }

	/// <summary>The method as metadata spells it.</summary>
	public required string MethodName { get; init; }

	/// <summary>The same method as a person says it, accessors and constructors named as such.</summary>
	public required string DisplayName { get; init; }

	/// <summary>
	/// The parameter names in brackets, as <c>(seed, text)</c>.
	/// <para>
	/// It is what tells two overloads apart in a list, since they share a name and therefore share
	/// the location string as well. Names rather than types because metadata has them to hand and a
	/// person picking from a list recognises the name they wrote.
	/// </para>
	/// </summary>
	public required string Signature { get; init; }

	/// <summary>The method-def token.</summary>
	public required int Token { get; init; }

	/// <summary>
	/// Whether the module's symbols are on this machine, which decides whether a position inside the
	/// method can be picked or only its entry. Reported rather than filtered on: a method with no
	/// symbols is still worth breaking at.
	/// </summary>
	public required bool HasSymbols { get; init; }

	/// <summary>How well it matched, lower being better. See <see cref="MethodQuery.Rank"/>.</summary>
	public required int Rank { get; init; }
}
