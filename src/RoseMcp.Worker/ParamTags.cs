using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.Worker;

/// <summary>
/// Keeps a declaration's <c>param</c> tags in step with its parameters.
/// <para>
/// Not a tidiness pass. Where a project generates its documentation file, a tag for a parameter that
/// no longer exists is CS1572 and a parameter with no tag is CS1573, and a repository that treats
/// warnings as errors -- this one does -- fails the build on both. So a signature change that left
/// the tags alone would compile the code and break the build, on precisely the members somebody
/// cared enough about to document.
/// </para>
/// <para>
/// Done over the text of the leading trivia rather than through the documentation syntax model,
/// because the shape being edited is a line: the indentation, the <c>///</c> and the line ending all
/// have to come out exactly as the neighbouring tags have them, and the easiest way to be sure of
/// that is to copy a neighbour.
/// </para>
/// </summary>
public static class ParamTags
{
	/// <summary>
	/// The declaration's leading trivia with the tags brought into line, or null when there is
	/// nothing to do -- which includes a documented member that leaves parameters it keeps
	/// undocumented, since adding a tag for a new one alone would raise CS1573 for each of those.
	/// </summary>
	/// <param name="leading">The declaration's leading trivia, documentation comment included.</param>
	/// <param name="removed">Parameters the change takes away.</param>
	/// <param name="added">Parameters the change brings in, by name.</param>
	/// <param name="kept">Parameters that were there before the change and still are.</param>
	/// <param name="order">
	/// Every parameter the declaration has after the change, in order and by its own names, which is
	/// what puts a new tag beside the tags of the parameters either side of it.
	/// </param>
	/// <param name="notes">Where to say what was done, or could not be.</param>
	public static SyntaxTriviaList? Update(
		SyntaxTriviaList leading,
		IReadOnlyList<string> removed,
		IReadOnlyList<string> added,
		IReadOnlyList<string> kept,
		IReadOnlyList<string> order,
		List<string> notes)
	{
		var lines = leading.ToFullString().Split('\n').ToList();

		// A member that documents no parameter gets a tag only where every parameter it will have is
		// one this adds: a documented method that took nothing and now takes something. Its comment
		// then describes all its parameters, as the tool promises, and nothing goes undocumented
		// beside a tag. Where it keeps undocumented parameters, a tag for the new one alone would
		// turn a comment no diagnostic fires on into CS1573 for each of them.
		var documentsParameters = lines.Any(line => TagAt(line) >= 0);
		var documentsAll = kept.Count == 0 && added.Count > 0 && lines.Any(IsSummaryEnd);

		if (!documentsParameters && !documentsAll) return null;

		var changed = false;

		foreach (var name in removed)
		{
			var index = lines.FindIndex(line => NameAt(line) == name);
			if (index < 0) continue;

			// A tag that does not close on its own line is a paragraph somebody wrote, and cutting it
			// at a line boundary would leave half of it behind.
			if (!Closes(lines[index]))
			{
				notes.Add($"The param tag for '{name}' spans more than one line, so it was left alone. "
					+ "Remove it by hand, or the build will fail on CS1572.");
				continue;
			}

			lines.RemoveAt(index);
			changed = true;
		}

		// In the order the parameters will have rather than the order they were asked for, so that two
		// new parameters side by side come out side by side: the second finds the tag just written for
		// the first and goes after it.
		var inOrder = order
			.Where(name => added.Contains(name, StringComparer.Ordinal))
			.Concat(added.Where(name => !order.Contains(name, StringComparer.Ordinal)));

		foreach (var name in inOrder)
		{
			if (lines.Any(line => NameAt(line) == name)) continue;

			var (line, before) = Placement(lines, order, name);

			if (line < 0)
			{
				notes.Add($"There was nowhere safe to put a param tag for '{name}'. Add one by hand, or "
					+ "the build will fail on CS1573.");
				continue;
			}

			var ending = EndingOf(lines);

			if (before)
			{
				lines.Insert(line, Modelled(lines[line], name, ending));
			}
			else
			{
				InsertAfter(lines, line, name, ending);
			}

			changed = true;

			notes.Add($"Added an empty param tag for '{name}'; it needs a description, which is not "
				+ "something this can invent.");
		}

		if (!changed) return null;

		return SyntaxFactory.ParseLeadingTrivia(string.Join("\n", lines));
	}

	/// <summary>
	/// A new tag built on the pattern of an existing line, so its indentation, its <c>///</c> and its
	/// line ending are the file's rather than this code's idea of them.
	/// <para>
	/// The prefix stops at the marker rather than at whatever the model line says next. A summary's
	/// closing line is a legitimate model and has no tag on it to stop at, and a tag with prose in
	/// front of it would otherwise have that prose copied into the new one.
	/// </para>
	/// <para>
	/// The ending comes from the comment rather than from the model line, because a line that happens
	/// to be the last one in the trivia has no ending of its own -- so reading it there answers a
	/// question about position and writes a bare line feed into a file that uses CR LF.
	/// </para>
	/// </summary>
	private static string Modelled(string existing, string name, string ending)
	{
		var marker = existing.IndexOf("///", StringComparison.Ordinal);
		var prefix = marker < 0 ? "/// " : existing[..(marker + 3)] + " ";

		return $"{prefix}<param name=\"{name}\"></param>{ending}";
	}

	/// <summary>
	/// The ending this comment uses, taken from any line that has one rather than from a particular
	/// line. The last line of a trivia list has none, and that says nothing about the file.
	/// </summary>
	private static string EndingOf(List<string> lines) =>
		lines.Any(line => line.EndsWith('\r')) ? "\r" : string.Empty;

	/// <summary>
	/// The parameter a param tag on this line documents, or null when the line opens no such tag.
	/// <para>
	/// Matched as a whole tag rather than as six characters, because <c>&lt;paramref&gt;</c> begins
	/// with the same six and is prose inside another tag rather than a tag of its own. Read as six,
	/// a sentence in the summary counted as the last tag there was: a new tag was written after it,
	/// inside the summary and on that sentence's own pattern, so the sentence appeared twice; a
	/// parameter the summary happened to mention was taken as documented already and never given a
	/// tag at all, which is CS1573; and removing that parameter took the sentence with it.
	/// </para>
	/// </summary>
	private static string? NameAt(string line)
	{
		var opening = TagAt(line);
		if (opening < 0) return null;

		var attribute = line.IndexOf("name=\"", opening, StringComparison.Ordinal);
		var close = line.IndexOf('>', opening);

		if (attribute < 0 || (close >= 0 && attribute > close)) return null;

		var start = attribute + "name=\"".Length;
		var end = line.IndexOf('"', start);

		return end < 0 ? null : line[start..end];
	}

	/// <summary>
	/// Where a param tag opens on this line, or -1. A letter straight after it makes it a tag of some
	/// other name, which <c>&lt;paramref&gt;</c> is.
	/// </summary>
	private static int TagAt(string line)
	{
		const string Opening = "<param";

		for (var index = line.IndexOf(Opening, StringComparison.Ordinal);
			index >= 0;
			index = line.IndexOf(Opening, index + 1, StringComparison.Ordinal))
		{
			var after = index + Opening.Length;

			if (after >= line.Length || !char.IsLetter(line[after])) return index;
		}

		return -1;
	}

	/// <summary>
	/// Where the tag for a new parameter goes: after the line where the tag of the nearest parameter
	/// before it closes, else before the line where the tag of the nearest parameter after it opens,
	/// else wherever <see cref="Anchor"/> says. <c>Before</c> says which side of <c>Line</c> it goes;
	/// a line of -1 is nowhere.
	/// <para>
	/// Beside its neighbours rather than after the last tag, because a parameter added in the middle of
	/// a list and documented at the end of it reads as the last parameter, and the fix is the reorder by
	/// hand this tool was called to save. The nearest neighbour that has a tag, not the immediate one,
	/// since a neighbour with no tag gives the new one nothing to stand next to.
	/// </para>
	/// <para>
	/// A neighbour's tag is wherever the comment has put it, so tags written in an order the declaration
	/// does not use stay in that order and the new one goes next to the tag of the parameter beside it.
	/// Reordering documentation nobody asked to reorder is a diff to read for nothing.
	/// </para>
	/// </summary>
	private static (int Line, bool Before) Placement(List<string> lines, IReadOnlyList<string> order, string name)
	{
		var position = -1;

		for (var index = 0; index < order.Count; index++)
		{
			if (order[index] == name)
			{
				position = index;
				break;
			}
		}

		if (position < 0) return (Anchor(lines), false);

		for (var index = position - 1; index >= 0; index--)
		{
			var opening = OpeningOf(lines, order[index]);
			if (opening >= 0) return (ClosingFrom(lines, opening), false);
		}

		for (var index = position + 1; index < order.Count; index++)
		{
			var opening = OpeningOf(lines, order[index]);
			if (opening >= 0) return (opening, true);
		}

		return (Anchor(lines), false);
	}

	/// <summary>
	/// The line a new tag goes after when no parameter beside it has a tag: the line the last param tag
	/// closes on, else the line the summary closes on, else nowhere.
	/// <para>
	/// After the summary when there is no tag left, which is what renaming a member's only parameter
	/// looks like from here -- the removal takes the only tag and the addition then has nothing to
	/// anchor on, so without the summary the new name never gets a tag and the build fails on CS1573.
	/// </para>
	/// </summary>
	private static int Anchor(List<string> lines)
	{
		var opening = lines.FindLastIndex(line => TagAt(line) >= 0);

		return opening < 0 ? lines.FindLastIndex(IsSummaryEnd) : ClosingFrom(lines, opening);
	}

	/// <summary>The line the tag documenting <paramref name="name"/> opens on, or -1.</summary>
	private static int OpeningOf(List<string> lines, string name) => lines.FindIndex(line => NameAt(line) == name);

	/// <summary>
	/// The line a tag opening on <paramref name="opening"/> closes on, or the opening line when nothing
	/// after it closes.
	/// <para>
	/// A new tag goes after the line a tag closes on, never the line it opens on. A tag whose
	/// description runs to a second line opens on one and closes on a later one, so anchoring where it
	/// opens writes the new tag into the middle of its prose -- and, taking its pattern from the line it
	/// lands after, copies that line's words into itself.
	/// </para>
	/// </summary>
	private static int ClosingFrom(List<string> lines, int opening)
	{
		for (var index = opening; index < lines.Count; index++)
		{
			if (Closes(lines[index])) return index;
		}

		return opening;
	}

	/// <summary>
	/// Writes a tag for <paramref name="name"/> on the line after <paramref name="anchor"/>, modelled
	/// on it.
	/// <para>
	/// The last line of a trivia list legitimately ends without a line ending, and an anchor lands on it
	/// whenever the tag it found is the last thing the comment says. That line stops being the last, so
	/// it takes the comment's ending, and the new line, now the last, goes without one.
	/// </para>
	/// </summary>
	private static void InsertAfter(List<string> lines, int anchor, string name, string ending)
	{
		var isLast = anchor == lines.Count - 1;
		var needsEnding = isLast && !lines[anchor].EndsWith('\r');

		if (needsEnding) lines[anchor] += ending;

		lines.Insert(anchor + 1, Modelled(lines[anchor], name, isLast ? string.Empty : ending));
	}

	private static bool IsSummaryEnd(string line) => line.Contains("</summary>", StringComparison.Ordinal);

	private static bool Closes(string line) =>
		line.Contains("</param>", StringComparison.Ordinal) || line.Contains("/>", StringComparison.Ordinal);
}
