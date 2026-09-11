namespace RoseMcp.Symbols;

/// <summary>
/// Reads the source a PDB points at, so a method can be shown and a line in it picked.
/// <para>
/// The path comes from the build machine. It is right whenever the module was built here, which is
/// the case somebody debugging their own code is in; it is a path that never existed on this machine
/// for anything out of a package or off a build agent. Both are ordinary, so a missing file is a
/// sentence rather than a failure.
/// </para>
/// </summary>
public static class SourceLines
{
	/// <summary>
	/// The most lines to hand back for one method. Generous enough for anything hand-written, and
	/// there to bound a region computed from sequence points -- a generated file's single method can
	/// span tens of thousands of lines, and nobody picks a breakpoint by scrolling one.
	/// </summary>
	public const int MaxLines = 600;

	/// <summary>Files past this size are not read; no hand-written source reaches it.</summary>
	private const long MaxFileBytes = 8 * 1024 * 1024;

	/// <summary>
	/// The lines a method covers, with a little either side so its signature and closing brace are
	/// in view.
	/// </summary>
	/// <param name="path">The file as the PDB records it.</param>
	/// <param name="firstLine">The first line of interest, counting from one.</param>
	/// <param name="lastLine">The last line of interest.</param>
	/// <param name="context">How many lines either side to include.</param>
	public static SourceExcerpt Read(string path, int firstLine, int lastLine, int context = 2)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return Missing(1, "The symbols name no file for this method.");
		}

		var from = Math.Max(1, firstLine - context);

		try
		{
			var file = new FileInfo(path);
			if (!file.Exists)
			{
				return Missing(
					from,
					$"{path} is not on this machine. That is the path recorded when the module was built, "
						+ "so it names a directory on whichever machine built it.");
			}

			if (file.Length > MaxFileBytes)
			{
				return Missing(from, $"{path} is {file.Length / (1024 * 1024)} MB, which is too large to read as source.");
			}

			var all = File.ReadAllLines(path);
			var to = Math.Min(all.Length, Math.Max(lastLine + context, from));
			if (from > all.Length)
			{
				return Missing(
					from,
					$"{path} has {all.Length} lines and the symbols place this method at line {firstLine}. "
						+ "The file on this machine is not the one the module was built from.");
			}

			var taken = Math.Min(to - from + 1, MaxLines);
			var lines = new string[taken];
			Array.Copy(all, from - 1, lines, 0, taken);

			return new SourceExcerpt
			{
				FirstLine = from,
				Lines = lines,
				Problem = taken < to - from + 1
					? $"Showing the first {MaxLines} lines of a method spanning {to - from + 1}."
					: null,
			};
		}
		catch (Exception exception)
		{
			return Missing(from, $"{path} could not be read: {exception.Message}");
		}
	}

	private static SourceExcerpt Missing(int firstLine, string problem) =>
		new() { FirstLine = firstLine, Lines = [], Problem = problem };
}
