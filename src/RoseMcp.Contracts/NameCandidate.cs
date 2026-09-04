namespace RoseMcp.Contracts;

/// <summary>One thing an unresolved name could turn out to be, and the import that would make it so.</summary>
public sealed record NameCandidate
{
	/// <summary>The namespace to import. This is the value to pass to rose_add_using or to usings.</summary>
	public required string Namespace { get; init; }

	/// <summary>
	/// What was found there, fully qualified, so two candidates for one name can be told apart by
	/// reading rather than by guessing from the namespace.
	/// </summary>
	public required string Symbol { get; init; }

	/// <summary>Class, Struct, Interface, Enum, Delegate, or ExtensionMethod.</summary>
	public required string Kind { get; init; }

	/// <summary>The assembly it lives in, which for a source type is its project's.</summary>
	public required string Assembly { get; init; }

	/// <summary>
	/// How many type arguments it takes. Reported because a name that resolves at arity 1 and is
	/// used at arity 2 is an error no import will fix, and the two are indistinguishable from the
	/// name alone.
	/// </summary>
	public int Arity { get; init; }

	/// <summary>True when it is declared in this solution rather than in a referenced assembly.</summary>
	public bool InSource { get; init; }

	/// <summary>
	/// Why importing this would change nothing, or null when it would. Said rather than filtered
	/// out: a caller looking at a name that will not resolve needs to know the namespace is already
	/// there, because that means the problem is something else entirely.
	/// </summary>
	public string? AlreadyInScope { get; init; }

	/// <summary>
	/// Why an import alone will not do, or null when it will -- a nested type that has to be written
	/// through its container, or an arity that does not match how the name was used.
	/// </summary>
	public string? Caveat { get; init; }
}
