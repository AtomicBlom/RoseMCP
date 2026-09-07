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
	/// nothing to do -- which includes the common case of a member that documents no parameters,
	/// since neither diagnostic fires on one.
	/// </summary>
	public static SyntaxTriviaList? Update(
		SyntaxTriviaList leading,
		IReadOnlyList<string> removed,
		IReadOnlyList<string> added,
		List<string> notes)
	{
		var lines = leading.ToFullString().Split('\n').ToList();

		// Neither CS1572 nor CS1573 fires on a member that documents no parameter at all, so there is
		// nothing here to keep in step.
		if (!lines.Any(line => TagAt(line) >= 0)) return null;

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

		foreach (var name in added)
		{
			if (lines.Any(line => NameAt(line) == name)) continue;

			var anchor = Anchor(lines);

			if (anchor < 0)
			{
				notes.Add($"There was nowhere safe to put a param tag for '{name}'. Add one by hand, or "
					+ "the build will fail on CS1573.");
				continue;
			}

			var ending = EndingOf(lines);

			// The line the tag goes after stops being the last one, so it needs the ending a last line
			// does not have. Only where the comment has one to give: the last line of a trivia list
			// legitimately ends without one, and that is the line an anchor lands on whenever the tag it
			// found is the last thing the comment says.
			if (ending.Length > 0 && !lines[anchor].EndsWith('\r')) lines[anchor] += ending;

			lines.Insert(anchor + 1, Modelled(lines[anchor], name, ending));
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
	/// The line a new tag goes after: the last param tag there is, else the line the summary closes
	/// on, else nowhere.
	/// <para>
	/// After the last tag rather than in declaration order, because a member's tags are not always in
	/// that order and reordering documentation nobody asked to reorder is a diff to read for nothing.
	/// After the summary when there is no tag left, which is what renaming a parameter looks like from
	/// here -- the removal takes the only tag and the addition then has nothing to anchor on, so the
	/// new name never gets a tag and the build fails on CS1573.
	/// </para>
	/// </summary>
	private static int Anchor(List<string> lines)
	{
		var tag = lines.FindLastIndex(line => TagAt(line) >= 0);

		return tag >= 0 ? tag : lines.FindLastIndex(line => line.Contains("</summary>", StringComparison.Ordinal));
	}

	private static bool Closes(string line) =>
		line.Contains("</param>", StringComparison.Ordinal) || line.Contains("/>", StringComparison.Ordinal);
}
