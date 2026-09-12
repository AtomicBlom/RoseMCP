namespace RoseMcp.Contracts;

/// <summary>
/// Whether a frame's module had symbols to read, and if not, why. It is what tells a reader how to
/// take a <c>local_0</c> or a frame with no line.
/// <para>
/// Said as a state rather than left to be inferred from an absence, because the same absence has
/// four causes and they send a reader to four different places. On a <see cref="Resolved"/> frame a
/// slot-named local is a compiler temporary and there is nothing to fix; on a
/// <see cref="NoSymbols"/> one it is a missing PDB and there is.
/// </para>
/// </summary>
public enum LiveSymbolState
{
	/// <summary>Symbols were read and describe this method. Names and lines are the source's own.</summary>
	Resolved,

	/// <summary>
	/// The module names no PDB, or names one that is not there. Ordinary for a framework assembly,
	/// and nothing to act on beyond knowing the names are slots.
	/// </summary>
	NoSymbols,

	/// <summary>
	/// A PDB was found and belongs to a different build of this module, so it is refused. The one
	/// case worth acting on: a rebuild without a redeploy, or a stale copy beside the binary.
	/// </summary>
	SymbolsMismatched,

	/// <summary>
	/// Symbols loaded, and this instruction maps to no source line -- compiler-generated IL, or an
	/// offset before the method's first statement. Names are still the source's own.
	/// </summary>
	NoSequencePoint,
}
