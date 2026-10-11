namespace RoseMcp.Server;

/// <summary>What changed between the surface a client holds and a broker's.</summary>
/// <param name="Added">Tools the newer surface has and the older did not, by name.</param>
/// <param name="Removed">Tools the older surface had and the newer does not.</param>
/// <param name="Changed">Tools both have, defined differently.</param>
/// <param name="InstructionsChanged">Whether the server instructions differ.</param>
public sealed record ToolSurfaceChange(
	IReadOnlyList<string> Added,
	IReadOnlyList<string> Removed,
	IReadOnlyList<string> Changed,
	bool InstructionsChanged)
{
	/// <summary>Whether any tool was added, removed or redefined, leaving the instructions aside.</summary>
	public bool ToolsChanged => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;

	/// <summary>Whether a client holding the older surface holds everything the newer one says.</summary>
	public bool IsEmpty => !ToolsChanged && !InstructionsChanged;
}
