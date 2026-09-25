using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// The lines an edit changed that nothing it was asked to do reaches.
/// <para>
/// Every writing tool promises to change what it was named and nothing else, and none of them could
/// show it. A tool that reflows the body it inserts into, or pulls a bracket up onto the line above
/// the element it was asked to change, still reports success, still compiles and still passes
/// <c>dotnet format</c>, because layout the formatter has no rule about is layout nothing checks. A
/// caller could then tell a good write from a bad one only by reading the file back, and a caller
/// reading every file back has no reason left to prefer these tools to a text edit whose result it
/// already knows. Measured from the two texts instead, it is a sentence in the result.
/// </para>
/// <para>
/// Lines rather than tokens, because a line is what a reviewer reads and what a diff shows: an edit
/// that changed no token but moved one onto another line is exactly the damage this exists to name. A
/// line is asked for when a span the tool declared overlaps it. An empty span is a place where
/// something goes: at the start of a line it asks only for what goes in before that line, and inside
/// one it asks for the line, since whatever goes in there rewrites it.
/// </para>
/// <para>
/// Lines that went in are asked for when they sit beside a line that was, or go where an empty span put
/// them. Lines that came out are named one by one, and what replaced them is not counted again. Blank
/// lines beside what was asked go with it, both ways: which of two identical blank lines a diff calls
/// the old one is arbitrary, so an edit that adds or drops the blank line between itself and a
/// neighbour is the same edit whichever one the diff picks.
/// </para>
/// </summary>
public sealed class Overreach
{
	/// <summary>How many places a sentence names before it counts the rest instead.</summary>
	private const int Named = 4;

	private Overreach(IReadOnlyList<int> lines, IReadOnlyList<(int After, int Count)> insertions)
	{
		Lines = lines;
		Insertions = insertions;
	}

	/// <summary>
	/// Lines of the file as it was, numbered from one, that the edit rewrote or removed with nothing it
	/// was asked to do reaching them.
	/// </summary>
	public IReadOnlyList<int> Lines { get; }

	/// <summary>
	/// Runs of lines that went in where nothing asked for them: the line of the file as it was that each
	/// run follows, zero for the top of the file, and how many lines it holds.
	/// </summary>
	public IReadOnlyList<(int After, int Count)> Insertions { get; }

	/// <summary>Whether the edit reached anywhere it was not asked to.</summary>
	public bool Any => Lines.Count > 0 || Insertions.Count > 0;

	/// <summary>
	/// What changed between <paramref name="before"/> and <paramref name="after"/> outside
	/// <paramref name="asked"/>, which is in the coordinates of <paramref name="before"/>.
	/// </summary>
	public static Overreach Of(SourceText before, SourceText after, IReadOnlyList<TextSpan> asked)
	{
		var map = TextMovement.LineMap(before, after);
		var lines = before.Lines;
		var count = lines.Count;

		var askedLines = new bool[count];
		var askedGaps = new bool[count + 1];

		foreach (var span in asked)
		{
			if (span.IsEmpty)
			{
				var line = lines.GetLineFromPosition(span.Start);

				if (span.Start == line.Start) askedGaps[line.LineNumber] = true;
				else askedLines[line.LineNumber] = true;

				continue;
			}

			var first = lines.GetLineFromPosition(span.Start).LineNumber;
			var last = lines.GetLineFromPosition(span.End - 1).LineNumber;

			for (var line = first; line <= last; line++) askedLines[line] = true;
		}

		var near = WithBlankLinesBeside(askedLines, askedGaps, lines);

		// A gap takes an insertion when it was asked for itself, or sits against a line that was.
		var insertable = new bool[count + 1];

		for (var gap = 0; gap <= count; gap++)
		{
			insertable[gap] = askedGaps[gap] || (gap > 0 && near[gap - 1]) || (gap < count && near[gap]);
		}

		var overreaching = new List<int>();
		var insertions = new List<(int After, int Count)>();

		// Each stretch between two lines that survived the edit is one hunk: the old lines between them
		// went, and the new lines between them came. The sentinels stand for the top and the bottom of
		// the file, which is where a hunk with no surviving line on one side begins or ends.
		var previousOld = -1;
		var previousNew = -1;

		for (var old = 0; old <= count; old++)
		{
			var survived = old < count ? map[old] : null;
			if (old < count && survived is null) continue;

			var nextNew = old < count ? survived!.Value : after.Lines.Count;

			var removed = Enumerable.Range(previousOld + 1, old - previousOld - 1).ToArray();
			var added = nextNew - previousNew - 1;

			overreaching.AddRange(removed.Where(line => !near[line]).Select(line => line + 1));

			// A hunk that removed lines rewrote them, and the lines that went in are what they became: the
			// removed ones are named above wherever nothing asked for them, and counting what replaced them
			// as well would say the same change twice. Only lines that went in with nothing going out are an
			// insertion in their own right.
			var intoAskedGap = Enumerable.Range(previousOld + 1, old - previousOld).Any(gap => insertable[gap]);

			if (added > 0 && removed.Length == 0 && !intoAskedGap) insertions.Add((previousOld + 1, added));

			previousOld = old;
			previousNew = nextNew;
		}

		return new Overreach(overreaching, insertions);
	}

	/// <summary>
	/// One sentence about every file the edit reached further into than it was asked to, for a result to
	/// carry. Nothing for a file it created, since nothing was there to be kept.
	/// </summary>
	internal static async Task<IReadOnlyList<string>> SentencesAsync(
		Solution before,
		Solution after,
		Asked asked,
		CancellationToken cancellationToken)
	{
		var sentences = new List<string>();

		// A file in a multi-targeted project is a document in each of its projects, and one file with one
		// edit, so it is said once.
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var project in after.GetChanges(before).GetProjectChanges())
		{
			foreach (var id in project.GetChangedDocuments())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var was = before.GetDocument(id);
				var now = after.GetDocument(id);
				if (was?.FilePath is not { Length: > 0 } path || now is null || !seen.Add(path)) continue;

				var reach = Of(
					await was.GetTextAsync(cancellationToken),
					await now.GetTextAsync(cancellationToken),
					asked.Of(path));

				if (reach.Sentence(Path.GetFileName(path)) is { } sentence) sentences.Add(sentence);
			}
		}

		return sentences;
	}

	/// <summary>
	/// What this says about one file, or null where the edit stayed inside what it was asked to change.
	/// </summary>
	/// <param name="name">The file's name, for a result that may be about several.</param>
	public string? Sentence(string name)
	{
		if (!Any) return null;

		var parts = new List<string>();

		if (Lines.Count > 0)
		{
			var ranges = Ranges(Lines);
			var spelled = ranges.Select(range => range.From == range.To ? $"{range.From}" : $"{range.From}-{range.To}");
			var one = ranges is [var only] && only.From == only.To;

			parts.Add($"{(one ? "line" : "lines")} {Listed([.. spelled])} changed");
		}

		if (Insertions.Count > 0)
		{
			var total = Insertions.Sum(insertion => insertion.Count);
			var places = Insertions.Select(insertion => insertion.After == 0 ? "at the top" : $"after line {insertion.After}");

			parts.Add($"{(total == 1 ? "a line" : $"{total} lines")} went in {Listed([.. places])}");
		}

		return $"{name}: {string.Join(" and ", parts)}. Nothing this was asked to do reaches them, so that is this "
			+ "tool's doing rather than anything the code needed: check them in the diff before keeping the change. "
			+ "The lines are numbered as the file was before the edit.";
	}

	/// <summary>
	/// The asked lines, and every blank line running on from one of them or from an asked gap.
	/// <para>
	/// Blank lines beside an edit go with it. Which of two identical blank lines a diff calls the old one
	/// is arbitrary, so an edit that adds or drops the blank line between itself and its neighbour is as
	/// likely to be paired with the one on the far side -- the same edit, which would otherwise be
	/// reported as a change nobody asked for. Adding a member, removing one, or starting a new group of
	/// imports all separate themselves this way, and a blank line is the one kind of line whose moving
	/// changes nothing a reader can see.
	/// </para>
	/// </summary>
	private static bool[] WithBlankLinesBeside(bool[] lines, bool[] gaps, TextLineCollection text)
	{
		var near = (bool[])lines.Clone();

		bool Blank(int line) => string.IsNullOrWhiteSpace(text[line].ToString());

		void Spread(int from, int step)
		{
			for (var line = from; line >= 0 && line < near.Length && Blank(line); line += step) near[line] = true;
		}

		for (var line = 0; line < lines.Length; line++)
		{
			if (!lines[line]) continue;

			Spread(line - 1, -1);
			Spread(line + 1, 1);
		}

		for (var gap = 0; gap < gaps.Length; gap++)
		{
			if (!gaps[gap]) continue;

			Spread(gap - 1, -1);
			Spread(gap, 1);
		}

		return near;
	}

	/// <summary>Consecutive line numbers as ranges, in order.</summary>
	private static (int From, int To)[] Ranges(IReadOnlyList<int> lines)
	{
		var ranges = new List<(int From, int To)>();

		foreach (var line in lines.Order())
		{
			if (ranges.Count > 0 && ranges[^1].To + 1 >= line)
			{
				ranges[^1] = (ranges[^1].From, Math.Max(ranges[^1].To, line));
				continue;
			}

			ranges.Add((line, line));
		}

		return [.. ranges];
	}

	/// <summary>
	/// "4", "4 and 9", "4, 7 and 12-15", or the first few and a count of the rest, which is what a
	/// caller holding a reflowed file wants from a sentence: where to start looking.
	/// </summary>
	private static string Listed(IReadOnlyList<string> places)
	{
		var spelled = places.Take(Named).ToList();

		if (places.Count > Named) spelled.Add($"{places.Count - Named} more place(s)");

		return spelled.Count == 1 ? spelled[0] : $"{string.Join(", ", spelled[..^1])} and {spelled[^1]}";
	}
}
