namespace RoseMcp.Symbols;

/// <summary>A stretch of a source file, or the reason there is not one.</summary>
public sealed record SourceExcerpt
{
	/// <summary>The line <see cref="Lines"/> starts at, counting from one.</summary>
	public required int FirstLine { get; init; }

	/// <summary>The text, one entry per line, with no terminators. Empty when it could not be read.</summary>
	public required IReadOnlyList<string> Lines { get; init; }

	/// <summary>
	/// Why there is no text, when there is none. A PDB records the path the build machine compiled
	/// from, so a module out of a package or off a build agent names a file this machine has never
	/// had -- which is ordinary rather than broken, and has to be said rather than shown as an empty
	/// listing.
	/// </summary>
	public string? Problem { get; init; }

	/// <summary>Whether there is text to show.</summary>
	public bool HasText => Lines.Count > 0;
}
