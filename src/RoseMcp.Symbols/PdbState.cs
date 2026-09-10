namespace RoseMcp.Symbols;

/// <summary>
/// Whether a module's symbols could be read, and if not, why. Said as a state rather than a null,
/// because the four cases send a reader to four different places.
/// </summary>
public enum PdbState
{
	/// <summary>Read, and describing this module. Names and lines are available.</summary>
	Loaded,

	/// <summary>
	/// The module names no PDB, or names one that is not there. An ordinary state for a release
	/// build or a framework assembly, and nothing to act on.
	/// </summary>
	NotFound,

	/// <summary>
	/// A PDB was found and it belongs to a different build of this module.
	/// <para>
	/// The one case worth shouting about, and the reason the identity is checked at all. A stale PDB
	/// reads perfectly well and answers with names and line numbers from code that is not running,
	/// which is the worst available failure: confident, plausible, and wrong. So it is refused and
	/// the caller falls back to slot numbers rather than being handed fiction.
	/// </para>
	/// </summary>
	Mismatched,

	/// <summary>A PDB was found and could not be read: truncated, locked, or not portable at all.</summary>
	Unreadable,
}
