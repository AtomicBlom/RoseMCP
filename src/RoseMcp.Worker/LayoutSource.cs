namespace RoseMcp.Worker;

/// <summary>
/// What decided part of a file's layout, most authoritative first: what the repository declares, then
/// what the file and the files beside it already do, then Roslyn's defaults where nothing said anything.
/// </summary>
public enum LayoutSource
{
	/// <summary>An .editorconfig covering the file, as Roslyn was given it or as it stands on disk.</summary>
	EditorConfig,

	/// <summary>The repository's attributes, which say how git writes the file's lines on checkout.</summary>
	GitAttributes,

	/// <summary>The file itself, as it was before anything was written to it.</summary>
	File,

	/// <summary>The files nearest it in its project, for a file with nothing of its own to read.</summary>
	Neighbours,

	/// <summary>Roslyn's own defaults: four spaces, and the platform's line ending.</summary>
	Default,
}
