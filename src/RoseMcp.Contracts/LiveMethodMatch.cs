namespace RoseMcp.Contracts;

/// <summary>One method a name search turned up, ready to set a breakpoint on or to look inside.</summary>
public sealed record LiveMethodMatch
{
	/// <summary>
	/// What to pass as a breakpoint's location: <c>Assembly!Namespace.Type.Method</c>. Always
	/// assembly-qualified, so something picked from a list of real methods cannot fail to bind over
	/// the guess the bare form makes about which module a namespace belongs to.
	/// </summary>
	public required string Location { get; init; }

	/// <summary>
	/// The method as a person says it, with accessors, constructors, lambdas and state machines named
	/// for what they were written as.
	/// </summary>
	public required string DisplayName { get; init; }

	/// <summary>
	/// The parameter names in brackets, which is what tells two overloads apart: they share a name,
	/// and therefore share the location as well.
	/// </summary>
	public required string Signature { get; init; }

	/// <summary>The assembly's simple name.</summary>
	public required string Module { get; init; }

	/// <summary>
	/// Whether the module's symbols are on this machine, which is what decides whether a position
	/// inside the method can be picked or only its entry broken at.
	/// </summary>
	public required bool HasSymbols { get; init; }
}
