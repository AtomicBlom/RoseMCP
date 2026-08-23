using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// What a mutation produced: the value to return, and the new solution when the mutation actually
/// changed something. A null solution means the mutation was a no-op and the revision holds.
/// </summary>
public readonly record struct MutationResult<T>(T Value, Solution? Solution);
