using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoseMcp.Worker;

/// <summary>
/// Reconciles a symbol one compilation produced with the compilation that is being asked about it.
/// <para>
/// A search over a project answers with symbols owned by whichever compilation declared them, so a
/// type from a referenced project can arrive as that project's symbol rather than as the asking
/// compilation's view of the same type. Most of Roslyn's compilation-scoped questions refuse one of
/// those by throwing rather than by answering, and the throw names the parameter rather than the
/// name the caller asked about -- "Parameter 'symbol' must be a symbol from this compilation or some
/// referenced assembly", which is an argument no caller of a rose_* tool ever sent.
/// </para>
/// </summary>
public static class CompilationSymbols
{
	/// <summary>
	/// The asking compilation's own view of a symbol, or null when it has none.
	/// <para>
	/// Mapping across rather than passing over what looks foreign, because the two are not the same
	/// answer. A type the asking project genuinely does reference still arrives as another
	/// compilation's symbol, so filtering alone reports a type in a project this one does not
	/// reference -- about a type it references. A symbol that will not map is the unreachable case,
	/// and no import fixes that one.
	/// </para>
	/// </summary>
	public static ISymbol? AsSeenBy(Compilation compilation, ISymbol symbol, CancellationToken cancellationToken)
	{
		if (Holds(compilation, symbol)) return symbol;

		return SymbolFinder.FindSimilarSymbols(symbol, compilation, cancellationToken).FirstOrDefault();
	}

	/// <summary>
	/// Whether the compilation can be asked about this symbol as it stands, which is the common case
	/// and the cheap one. Mapping every candidate instead would resolve a symbol key per match, and a
	/// member search for a name like <c>Count</c> matches thousands.
	/// </summary>
	public static bool Holds(Compilation compilation, ISymbol symbol) =>
		symbol.ContainingAssembly is { } assembly
		&& (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)
			|| compilation.GetMetadataReference(assembly) is not null);
}
