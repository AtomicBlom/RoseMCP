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
	/// Every terminator a text file can hold, in the order ties are broken. Exhaustive, since
	/// <see cref="Terminators"/> produces nothing else, so iterating it needs no dictionary and gives
	/// the same answer whatever order the file happens to hold them in.
	/// </summary>
	private static readonly string[] Kinds = ["\r\n", "\n", "\r"];

	/// <summary>
	/// How many lines changed terminator and what they changed to, or null where none did.
	/// <para>
	/// Counted per kind rather than compared line by line, because a positional comparison lines up
	/// only while the two files have the same lines. Remove an import above a literal written with
	/// bare LFs and every position from there on is offset by one: each of the file's CRLFs is then
	/// compared against an LF that has been inside that literal all along, and a CRLF repository is
	/// told its endings were rewritten to LF. Nothing had been. That is the one message a caller
	/// relies on to know what happened to their endings, and a wrong direction there is worse than
	/// silence.
	/// </para>
	/// <para>
	/// Counting says it without needing the lines to correspond. A kind that lost terminators is a
	/// kind lines were rewritten away from, and the kind that gained the most is where they went, so
	/// the count is of endings that stopped being what they were. A file that only gained lines has
	/// lost nothing and a file that only lost lines has gained nothing, and neither is reported --
	/// both are changes a diff shows in full, which is the whole reason this exists for the one it
	/// cannot show.
	/// </para>
	/// </summary>
	/// <param name="before">The file as it was.</param>
	/// <param name="after">The file as it is now.</param>
	public static (int Lines, string To)? Changed(string before, string after)
	{
		var was = Terminators(before);
		var now = Terminators(after);

		var lost = 0;
		var gained = 0;
		var to = string.Empty;

		foreach (var kind in Kinds)
		{
			var moved = Count(now, kind) - Count(was, kind);

			if (moved < 0) lost -= moved;
			if (moved <= gained) continue;

			gained = moved;
			to = kind;
		}

		if (lost == 0 || to.Length == 0) return null;

		return (lost, Name(to));
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

	/// <summary>How many of <paramref name="endings"/> are <paramref name="kind"/>.</summary>
	private static int Count(IReadOnlyList<string> endings, string kind) =>
		endings.Count(ending => string.Equals(ending, kind, StringComparison.Ordinal));
}
