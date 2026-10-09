namespace RoseMcp.Contracts;

/// <summary>
/// An argument a call carried under a name its tool does not declare, and the declared names it
/// most likely meant.
/// </summary>
/// <param name="Name">The name as the caller sent it.</param>
/// <param name="Closest">
/// The declared names nearest to it, in the order the schema declares them. More than one only when
/// they are equally near, and empty when nothing declared is close enough to be a misspelling.
/// </param>
public sealed record UndeclaredArgument(string Name, IReadOnlyList<string> Closest);
