using System.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Renders a unified diff between two versions of a file.
/// <para>
/// A refactoring that reports only "changed 7 files" asks to be trusted. A diff lets the caller
/// check. Implemented here rather than pulled in as a dependency because the requirement is modest:
/// readable output for a human or a model, not byte-exact GNU compatibility.
/// </para>
/// </summary>
public static class UnifiedDiff
{
	private const int ContextLines = 3;

	public static string Render(string path, string before, string after) => Compare(path, before, after).Text;

	/// <summary>
	/// The diff between two versions of a file, and the lines it changed as the file now reads them.
	/// <para>
	/// Both from one comparison, because the comparison is the expensive part and the lines are what a
	/// result carries by default: an applied write names where it landed rather than echoing what the
	/// caller sent.
	/// </para>
	/// </summary>
	public static FileDiff Compare(string path, string before, string after)
	{
		var oldLines = SplitLines(before);
		var newLines = SplitLines(after);
		var operations = Diff(oldLines, newLines);

		var hunks = Group(operations);
		if (hunks.Count == 0) return new FileDiff(string.Empty, null);

		var output = new StringBuilder();
		output.Append("--- ").Append(path).Append('\n');
		output.Append("+++ ").Append(path).Append('\n');

		foreach (var hunk in hunks)
		{
			var oldCount = hunk.Count(operation => operation.Kind != OperationKind.Insert);
			var newCount = hunk.Count(operation => operation.Kind != OperationKind.Delete);

			output.Append("@@ -").Append(hunk[0].OldLine + 1).Append(',').Append(oldCount)
				.Append(" +").Append(hunk[0].NewLine + 1).Append(',').Append(newCount).Append(" @@\n");

			foreach (var operation in hunk)
			{
				var marker = operation.Kind switch
				{
					OperationKind.Insert => '+',
					OperationKind.Delete => '-',
					_ => ' ',
				};

				output.Append(marker).Append(operation.Text).Append('\n');
			}
		}

		return new FileDiff(output.ToString(), ChangedLines(operations, LineCount(newLines)));
	}

	/// <summary>
	/// The lines an edit changed, numbered as the file now reads, spelled as ranges: <c>174-200</c>, or
	/// <c>3, 174-200</c> for an import and a member. An inserted line is itself; a removed one is
	/// the line now standing where it was. Past <see cref="RangesNamed"/> ranges the rest are counted,
	/// since a reformatted file would otherwise list every other line.
	/// </summary>
	/// <param name="operations">The comparison, in order.</param>
	/// <param name="lineCount">How many lines the file now has, so a removal at the end names its last line.</param>
	private static string? ChangedLines(List<Operation> operations, int lineCount)
	{
		var lines = new SortedSet<int>();

		foreach (var operation in operations)
		{
			if (operation.Kind == OperationKind.Equal) continue;

			var line = Math.Clamp(operation.NewLine + 1, 1, Math.Max(lineCount, 1));
			lines.Add(line);
		}

		return lines.Count == 0 ? null : Spelled(lines);
	}

	/// <summary>Consecutive line numbers as ranges, the first <see cref="RangesNamed"/> of them by number.</summary>
	private static string Spelled(IEnumerable<int> lines)
	{
		var ranges = new List<(int From, int To)>();

		foreach (var line in lines)
		{
			var extends = ranges.Count > 0 && ranges[^1].To + 1 >= line;

			if (extends) ranges[^1] = (ranges[^1].From, Math.Max(ranges[^1].To, line));
			else ranges.Add((line, line));
		}

		var named = ranges
			.Take(RangesNamed)
			.Select(range => range.From == range.To ? $"{range.From}" : $"{range.From}-{range.To}");
		var spelled = string.Join(", ", named);

		return ranges.Count > RangesNamed ? $"{spelled} and {ranges.Count - RangesNamed} more" : spelled;
	}

	/// <summary>
	/// How many ranges <see cref="ChangedLines"/> names before it counts the rest. An edit lands in one
	/// or two; a rename or a format reaching further is read with the diff, not the ranges.
	/// </summary>
	private const int RangesNamed = 6;

	/// <summary>
	/// A whole file as an addition. Diffing it against an empty string almost works, but that
	/// claims the file used to have a line in it, and reads as a change rather than a creation.
	/// </summary>
	public static string RenderNewFile(string path, string text)
	{
		var lines = SplitLines(text);
		var count = LineCount(lines);
		if (count == 0) return string.Empty;

		var output = new StringBuilder();
		output.Append("--- /dev/null\n");
		output.Append("+++ ").Append(path).Append('\n');
		output.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");

		for (var index = 0; index < count; index++)
		{
			output.Append('+').Append(lines[index]).Append('\n');
		}

		return output.ToString();
	}

	/// <summary>A whole file as an addition, with every line of it as the lines changed.</summary>
	public static FileDiff NewFile(string path, string text)
	{
		var count = LineCount(SplitLines(text));
		var lines = count switch
		{
			0 => null,
			1 => "1",
			_ => $"1-{count}",
		};

		return new FileDiff(RenderNewFile(path, text), lines);
	}

	private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

	/// <summary>
	/// How many lines a split holds. A file ending in a newline splits with a trailing empty element,
	/// which is not a line.
	/// </summary>
	private static int LineCount(string[] lines) =>
		lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

	/// <summary>
	/// Longest common subsequence over lines. Quadratic, which is fine: this only ever runs on files
	/// a refactoring actually touched, and a diff nobody can read is worse than a slow one.
	/// </summary>
	private static List<Operation> Diff(string[] oldLines, string[] newLines)
	{
		var lengths = new int[oldLines.Length + 1, newLines.Length + 1];

		for (var i = oldLines.Length - 1; i >= 0; i--)
		{
			for (var j = newLines.Length - 1; j >= 0; j--)
			{
				lengths[i, j] = oldLines[i] == newLines[j]
					? lengths[i + 1, j + 1] + 1
					: Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
			}
		}

		var operations = new List<Operation>();
		var x = 0;
		var y = 0;

		while (x < oldLines.Length && y < newLines.Length)
		{
			if (oldLines[x] == newLines[y])
			{
				operations.Add(new Operation(OperationKind.Equal, oldLines[x], x, y));
				x++;
				y++;
			}
			else if (lengths[x + 1, y] >= lengths[x, y + 1])
			{
				operations.Add(new Operation(OperationKind.Delete, oldLines[x], x, y));
				x++;
			}
			else
			{
				operations.Add(new Operation(OperationKind.Insert, newLines[y], x, y));
				y++;
			}
		}

		while (x < oldLines.Length)
		{
			operations.Add(new Operation(OperationKind.Delete, oldLines[x], x, y));
			x++;
		}

		while (y < newLines.Length)
		{
			operations.Add(new Operation(OperationKind.Insert, newLines[y], x, y));
			y++;
		}

		return operations;
	}

	/// <summary>Collects changes into hunks with surrounding context, dropping untouched stretches.</summary>
	private static List<List<Operation>> Group(List<Operation> operations)
	{
		var interesting = operations
			.Select((operation, index) => (operation, index))
			.Where(pair => pair.operation.Kind != OperationKind.Equal)
			.Select(pair => pair.index)
			.ToArray();

		if (interesting.Length == 0) return [];

		var hunks = new List<List<Operation>>();
		var start = Math.Max(0, interesting[0] - ContextLines);
		var end = Math.Min(operations.Count - 1, interesting[0] + ContextLines);

		foreach (var index in interesting.Skip(1))
		{
			if (index - ContextLines <= end + 1)
			{
				end = Math.Min(operations.Count - 1, index + ContextLines);
				continue;
			}

			hunks.Add(operations[start..(end + 1)]);
			start = Math.Max(0, index - ContextLines);
			end = Math.Min(operations.Count - 1, index + ContextLines);
		}

		hunks.Add(operations[start..(end + 1)]);
		return hunks;
	}

	private enum OperationKind
	{
		Equal,
		Insert,
		Delete,
	}

	private readonly record struct Operation(OperationKind Kind, string Text, int OldLine, int NewLine);
}
