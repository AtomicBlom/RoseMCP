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

	public static string Render(string path, string before, string after)
	{
		var oldLines = SplitLines(before);
		var newLines = SplitLines(after);
		var operations = Diff(oldLines, newLines);

		var hunks = Group(operations);
		if (hunks.Count == 0) return string.Empty;

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

		return output.ToString();
	}

	/// <summary>
	/// A whole file as an addition. Diffing it against an empty string almost works, but that
	/// claims the file used to have a line in it, and reads as a change rather than a creation.
	/// </summary>
	public static string RenderNewFile(string path, string text)
	{
		var lines = SplitLines(text);

		// A file ending in a newline splits with a trailing empty element, which is not a line.
		var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
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

	private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

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
