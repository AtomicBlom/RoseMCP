using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// What <see cref="SymbolResolver"/> found for an address: the symbols it reaches, and what is needed
/// to explain reaching none. A value rather than an answer, because what counts as one answer differs
/// between reading and writing, and only the caller knows which it is doing.
/// </summary>
public sealed record SymbolResolution
{
	/// <summary>The address as the caller wrote it.</summary>
	public required SymbolAddress Address { get; init; }

	/// <summary>
	/// Every symbol this solution's source declares at the address, under any of its readings. Several
	/// where it names overloads, the halves of a partial in different projects, or a type and its
	/// constructor; refusing or choosing between them is the caller's business.
	/// </summary>
	public required IReadOnlyList<ISymbol> Source { get; init; }

	/// <summary>
	/// Every source symbol carrying the address's last segment, wherever it is declared. Only for a
	/// refusal, which lists them so a caller who qualified a name wrongly can see where it is.
	/// </summary>
	public required IReadOnlyList<ISymbol> Named { get; init; }

	/// <summary>
	/// The source types a constructor reading reached, whether or not they declare a constructor. Empty
	/// when the address names no constructor.
	/// </summary>
	public required IReadOnlyList<INamedTypeSymbol> Constructed { get; init; }

	/// <summary>
	/// The one symbol a referenced assembly declares at the address, where source declares nothing
	/// there and the caller asked for metadata to be searched.
	/// </summary>
	public ISymbol? Metadata { get; init; }

	/// <summary>Whether referenced assemblies were searched, which a refusal says.</summary>
	public bool MetadataSearched { get; init; }
}
