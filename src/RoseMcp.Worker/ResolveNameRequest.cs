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

	public int MaxResults { get; init; } = 20;
}
