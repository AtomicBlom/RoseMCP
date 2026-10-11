namespace RoseMcp.Ui.Core.Views;

/// <summary>What one line of a unified diff is, which is what decides its colour.</summary>
public enum DiffLineKind
{
	/// <summary>Unchanged, shown for where the change sits.</summary>
	Context,

	/// <summary>A line the change added.</summary>
	Added,

	/// <summary>A line the change removed.</summary>
	Removed,

	/// <summary>A hunk's <c>@@</c> line, saying where in the file the lines below are.</summary>
	Hunk,

	/// <summary>The file names the diff opens with.</summary>
	Header,
}

/// <summary>One line of an edit's diff as a window draws it.</summary>
/// <param name="Text">The line, its marker included, so a copied diff is still a diff.</param>
/// <param name="Kind">What the line is.</param>
public sealed record DiffLine(string Text, DiffLineKind Kind)
{
	/// <summary>Whether the line was added.</summary>
	public bool IsAdded => Kind == DiffLineKind.Added;

	/// <summary>Whether the line was removed.</summary>
	public bool IsRemoved => Kind == DiffLineKind.Removed;

	/// <summary>Whether the line says where the lines below it are.</summary>
	public bool IsLocation => Kind is DiffLineKind.Hunk or DiffLineKind.Header;

	/// <summary>
	/// A unified diff, a line at a time. The headers are told apart from an added or removed line by their
	/// three-character markers, which a changed line cannot start with in a diff a tool wrote unless the code
	/// itself began with <c>++</c> -- and a line of code that does reads as added either way.
	/// </summary>
	public static IReadOnlyList<DiffLine> Parse(string? diff)
	{
		if (string.IsNullOrEmpty(diff)) return [];

		return
		[
			.. diff
				.Replace("\r\n", "\n", StringComparison.Ordinal)
				.TrimEnd('\n')
				.Split('\n')
				.Select(line => new DiffLine(line, KindOf(line))),
		];
	}

	private static DiffLineKind KindOf(string line)
	{
		if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) return DiffLineKind.Header;
		if (line.StartsWith("@@", StringComparison.Ordinal)) return DiffLineKind.Hunk;
		if (line.StartsWith('+')) return DiffLineKind.Added;
		if (line.StartsWith('-')) return DiffLineKind.Removed;

		return DiffLineKind.Context;
	}
}
