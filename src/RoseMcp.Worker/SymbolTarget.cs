using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// Which symbol a call is about, said either way.
/// <para>
/// A position is what a caller has when it is reading a file and pointing at something. A name is
/// what it has when it has not read the file, which is the case that matters: needing a line and
/// column means grepping for one first, and the position is wrong the moment an earlier edit lands.
/// A wrong position is worse on a read than on a write, because it is silent -- the identifier a
/// mis-counted column lands on is some other symbol, and the answer about it is complete,
/// well-formed and about the wrong thing.
/// </para>
/// <para>
/// Both are accepted because both are real, and neither is guessed at. A local variable or a
/// parameter is not a declaration a name search can reach, so a position is the only way to name
/// one, and that is what keeps it on every tool rather than only on the ones that could not manage
/// without it.
/// </para>
/// </summary>
public sealed record SymbolTarget
{
	/// <summary>The symbol by name, as Namespace.Type.Member, with a parameter list for an overload.</summary>
	public string? Symbol { get; init; }

	/// <summary>The file, with <see cref="Line"/> and <see cref="Column"/>; or which file, with a name.</summary>
	public string? FilePath { get; init; }

	public int? Line { get; init; }

	public int? Column { get; init; }

	public bool IsByName => !string.IsNullOrWhiteSpace(Symbol);

	public bool IsByPosition => !string.IsNullOrWhiteSpace(FilePath) && Line is not null && Column is not null;

	/// <summary>
	/// The symbol this names, however it named it. A target that says neither is an error rather than
	/// a default: guessing which of the two was meant would answer about some other symbol entirely,
	/// and answering confidently about the wrong symbol is the failure worth the most trouble to
	/// avoid.
	/// </summary>
	public async Task<ISymbol> ResolveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken)
	{
		if (IsByName)
		{
			var target = await DeclarationLocator.FindSymbolAsync(
				snapshot.Solution, Symbol!, FilePath, cancellationToken);

			return target.Symbol;
		}

		if (!IsByPosition)
		{
			throw new ArgumentException(
				"Name the symbol, as Namespace.Type.Member, or give filePath with line and column. "
					+ "A name needs no position and does not go stale when the file is edited. A local "
					+ "variable or a parameter is declared inside a member rather than as one, so it has no "
					+ "name to give here and needs the position.");
		}

		var (symbol, _) = await SymbolLocator.ResolveAsync(
			snapshot.Solution, FilePath!, Line!.Value, Column!.Value, cancellationToken);

		return symbol;
	}

	/// <summary>How to say what this target is about, for a progress line or an error.</summary>
	public string Describe() =>
		IsByName ? Symbol! : $"{Path.GetFileName(FilePath) ?? "?"}:{Line?.ToString() ?? "?"}";
}
