namespace RoseMcp.Worker;

/// <summary>
/// Nothing in the solution's source is reached by the address the caller wrote.
/// <para>
/// Its own type because it is the one refusal a read may answer another way. The condition is that
/// nothing the address <em>reaches</em> is declared here, which is wider than nothing carrying the
/// name and has to be: a solution of any size declares an Add, a Name and a Document of its own,
/// and a refusal that fired only on a name nobody uses would send every library member sharing one
/// to a decompiler while the compilation held the answer.
/// </para>
/// <para>
/// It is equally not every refusal. An ambiguous match is several declarations in source, and
/// answering from a referenced assembly while two declarations here carry the name would be a
/// confident answer about the wrong symbol -- the failure the ambiguity refusal exists to prevent.
/// Nor is a declaration ruled out by where it lives, in generated code or in a file other than the
/// one pinned, nor a constructor missing from a type this solution does declare: source reached
/// something in each of those, and a library type that happens to share its name is not what the
/// caller meant.
/// </para>
/// </summary>
public sealed class SymbolNotFoundException(string message) : ArgumentException(message);
