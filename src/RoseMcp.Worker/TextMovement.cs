using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Where a position in the text before an edit ended up in the text after it.
/// <para>
/// Worked out by diffing lines rather than from Roslyn's text changes, because those are only exact
/// when the new text was derived from the old one. A tool that rewrites a syntax root produces text
/// with no such lineage, and Roslyn then reports the whole file as one change -- which would leave no
/// position with anywhere to go.
/// </para>
/// <para>
/// A line that survived the edit unchanged keeps its column and moves by however many lines went in or
/// out above it. A line the edit rewrote has no answer: its text is gone, and anything claiming to know
/// where a position in it went would be inventing it.
/// </para>
/// </summary>
public sealed class TextMovement
{
	private readonly Dictionary<string, int?[]> _files;

	private TextMovement(Dictionary<string, int?[]> files)
	{
		_files = files;
	}

	/// <summary>No file changed, so every position is where it was.</summary>
	public static TextMovement None { get; } = new(new(StringComparer.OrdinalIgnoreCase));

	/// <summary>The movement one file's edit caused.</summary>
	public static TextMovement Of(string path, SourceText before, SourceText after) =>
		new(new(StringComparer.OrdinalIgnoreCase) { [path] = LineMap(before, after) });

	/// <summary>
	/// The movement every changed document went through. A file shared by several projects, as a
	/// multi-targeted one is, is diffed once: it is one file on disk, with one edit.
	/// </summary>
	public static async Task<TextMovement> BetweenAsync(Solution before, Solution after, CancellationToken cancellationToken)
	{
		var files = new Dictionary<string, int?[]>(StringComparer.OrdinalIgnoreCase);

		foreach (var project in after.GetChanges(before).GetProjectChanges())
		{
			foreach (var id in project.GetChangedDocuments())
			{
				var was = before.GetDocument(id);
				var now = after.GetDocument(id);
				if (was?.FilePath is not { Length: > 0 } path || now is null || files.ContainsKey(path)) continue;

				files[path] = LineMap(
					await was.GetTextAsync(cancellationToken),
					await now.GetTextAsync(cancellationToken));
			}
		}

		return new TextMovement(files);
	}

	/// <summary>
	/// Where a 1-based line and column in <paramref name="path"/> now are, or null when the edit rewrote
	/// that line.
	/// </summary>
	public (int Line, int Column)? Map(string? path, int line, int column)
	{
		if (path is null || !_files.TryGetValue(path, out var map)) return (line, column);

		if (line < 1 || line > map.Length) return null;

		return map[line - 1] is { } moved ? (moved + 1, column) : null;
	}

	/// <summary>
	/// For each line of <paramref name="before"/>, the index it has in <paramref name="after"/>, or null
	/// when it did not survive.
	/// <para>
	/// The lines both texts start and end with are matched first, and the longest common subsequence is
	/// run only over what lies between. An edit touches a few lines of a file, so that middle is small
	/// and the quadratic part stays cheap on a file of any length.
	/// </para>
	/// </summary>
	internal static int?[] LineMap(SourceText before, SourceText after)
	{
		var old = Lines(before);
		var updated = Lines(after);
		var map = new int?[old.Length];

		var prefix = 0;
		while (prefix < old.Length && prefix < updated.Length && old[prefix] == updated[prefix])
		{
			map[prefix] = prefix;
			prefix++;
		}

		var suffix = 0;
		while (suffix < old.Length - prefix && suffix < updated.Length - prefix
			&& old[old.Length - 1 - suffix] == updated[updated.Length - 1 - suffix])
		{
			map[old.Length - 1 - suffix] = updated.Length - 1 - suffix;
			suffix++;
		}

		var oldCount = old.Length - prefix - suffix;
		var newCount = updated.Length - prefix - suffix;
		var lengths = new int[oldCount + 1, newCount + 1];

		for (var i = oldCount - 1; i >= 0; i--)
		{
			for (var j = newCount - 1; j >= 0; j--)
			{
				lengths[i, j] = old[prefix + i] == updated[prefix + j]
					? lengths[i + 1, j + 1] + 1
					: Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
			}
		}

		var x = 0;
		var y = 0;

		while (x < oldCount && y < newCount)
		{
			if (old[prefix + x] == updated[prefix + y])
			{
				map[prefix + x] = prefix + y;
				x++;
				y++;
			}
			else if (lengths[x + 1, y] >= lengths[x, y + 1])
			{
				x++;
			}
			else
			{
				y++;
			}
		}

		return map;
	}

	private static string[] Lines(SourceText text) => [.. text.Lines.Select(line => line.ToString())];
}
