namespace RoseMcp.Worker;

/// <summary>
/// Nothing in the solution declares the name a caller wrote.
/// <para>
/// Its own type because it is the one refusal a read may answer another way. An ambiguous match is
/// several declarations in source and is never a reason to look in metadata: answering from a
/// referenced assembly while two declarations here carry the name would be a confident answer about
/// the wrong symbol, which is the failure the ambiguity refusal exists to prevent.
/// </para>
/// </summary>
public sealed class SymbolNotFoundException(string message) : ArgumentException(message);
