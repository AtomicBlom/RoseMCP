using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>Which name to resolve, and what is known about how it is being used.</summary>
public sealed record ResolveNameRequest
{
	/// <summary>
	/// The name as the code spells it. <c>Encoding</c>, <c>Encoding.UTF8</c> and <c>List&lt;int&gt;</c>
	/// all name something that has to resolve, and are all accepted.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// The file the name is used in. Optional, and worth supplying: it scopes the search to the
	/// project whose references the code can actually reach, and it is the only way to know which
	/// candidates are in scope there already.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>
	/// How many type arguments the use site supplies, when that is known. Taken from the name where
	/// it is written with them.
	/// </summary>
	public int? Arity { get; init; }

	/// <summary>
	/// How the code uses the name, where the caller read it off the code: a name called on its own is
	/// answered by no type, and one after a dot by no type either. A caller with only the name says
	/// nothing, and every kind of candidate is searched.
	/// </summary>
	public NameUse Use { get; init; } = NameUse.Any;

	/// <summary>
	/// The type of what the name is called on, where it is used after a dot and the caller bound it in the
	/// file's own compilation. An extension method answers only if it takes that type, which is the
	/// question the compiler's CS1061 asks: one that does not turns the error into another and leaves an
	/// import the call never needs. Null searches every extension of that name, as a caller with only the
	/// name has to.
	/// </summary>
	public ITypeSymbol? Receiver { get; init; }

	public int MaxResults { get; init; } = 20;
}
