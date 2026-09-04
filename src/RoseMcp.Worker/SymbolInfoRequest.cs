namespace RoseMcp.Worker;

/// <summary>
/// Which symbol to describe, said either way.
/// <para>
/// A position is what a caller has when it is reading a file and pointing at something. A name is
/// what it has when it has not read the file, which is the case that matters: needing a line and
/// column means grepping for one first, and the position is wrong the moment an earlier edit lands.
/// Both are accepted because both are real, and neither is guessed at -- one or the other, and
/// saying nothing is an error rather than a default.
/// </para>
/// </summary>
public sealed record SymbolInfoRequest
{
	/// <summary>The symbol by name, as Namespace.Type.Member, with a parameter list for an overload.</summary>
	public string? Symbol { get; init; }

	/// <summary>The file, with <see cref="Line"/> and <see cref="Column"/>; or which file, with a name.</summary>
	public string? FilePath { get; init; }

	public int? Line { get; init; }

	public int? Column { get; init; }

	public bool IsByName => !string.IsNullOrWhiteSpace(Symbol);

	public bool IsByPosition => !string.IsNullOrWhiteSpace(FilePath) && Line is not null && Column is not null;
}
