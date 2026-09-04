namespace RoseMcp.Worker;

/// <summary>
/// What changed about a file's line terminators, which is the one edit a unified diff cannot show.
/// <para>
/// A diff compares the content of lines, and a terminator is not content -- so a file whose every
/// LF became CRLF produces no hunk at all. That is the change <c>rose_format</c> makes most often,
/// and it makes it in exactly the repositories where it matters: where IDE0055 is an error, an LF in
/// a CRLF file is a failed build, and fixing it is the whole reason the call was made. Reporting
/// five files changed beside an empty diff reads precisely like a no-op.
/// </para>
/// </summary>
public static class LineEndings
{
	/// <summary>
	/// How many lines changed terminator and what they changed to, or null where none did.
	/// </summary>
	/// <param name="before">The file as it was.</param>
	/// <param name="after">The file as it is now.</param>
	public static (int Lines, string To)? Changed(string before, string after)
	{
		var was = Terminators(before);
		var now = Terminators(after);

		// Compared by position, which lines up only while the content is otherwise the same. Where
		// lines were added or removed the tail of this comparison is meaningless -- but it is also
		// not needed, because a change of that shape is one the diff already shows.
		var shared = Math.Min(was.Count, now.Count);
		var moved = new List<string>();

		for (var index = 0; index < shared; index++)
		{
			if (!string.Equals(was[index], now[index], StringComparison.Ordinal)) moved.Add(now[index]);
		}

		if (moved.Count == 0) return null;

		// The commonest of the new terminators. A file rewritten to two different endings at once is
		// not a thing any formatter does, and naming the majority beats naming whichever came last.
		var to = moved
			.GroupBy(ending => ending, StringComparer.Ordinal)
			.OrderByDescending(group => group.Count())
			.First().Key;

		return (moved.Count, Name(to));
	}

	/// <summary>The name a person would use, so a notice reads as advice rather than as escaping.</summary>
	public static string Name(string ending) => ending switch
	{
		"\r\n" => "CRLF",
		"\n" => "LF",
		"\r" => "CR",
		_ => "none",
	};

	/// <summary>
	/// Each line's own terminator, in order. The last line has none where the file does not end in a
	/// newline, and that absence is itself a difference worth catching.
	/// </summary>
	private static IReadOnlyList<string> Terminators(string text)
	{
		var endings = new List<string>();

		for (var index = 0; index < text.Length; index++)
		{
			if (text[index] is not ('\n' or '\r')) continue;

			var ending = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
				? "\r\n"
				: text[index].ToString();

			endings.Add(ending);
			index += ending.Length - 1;
		}

		return endings;
	}
}
