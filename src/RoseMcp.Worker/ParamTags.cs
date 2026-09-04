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
		var text = leading.ToFullString();
		if (!text.Contains("<param", StringComparison.Ordinal)) return null;

		var lines = text.Split('\n').ToList();
		var changed = false;

		foreach (var name in removed)
		{
			var index = lines.FindIndex(line => Mentions(line, name));
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
			if (lines.Any(line => Mentions(line, name))) continue;

			var last = lines.FindLastIndex(line => line.Contains("<param", StringComparison.Ordinal));
			if (last < 0) continue;

			lines.Insert(last + 1, Modelled(lines[last], name));
			changed = true;

			notes.Add($"Added an empty param tag for '{name}'; it needs a description, which is not "
				+ "something this can invent.");
		}

		if (!changed) return null;

		return SyntaxFactory.ParseLeadingTrivia(string.Join("\n", lines));
	}

	/// <summary>
	/// A new tag built on the pattern of an existing one, so its indentation, its <c>///</c> and its
	/// line ending are the file's rather than this code's idea of them.
	/// </summary>
	private static string Modelled(string existing, string name)
	{
		var opening = existing.IndexOf("<param", StringComparison.Ordinal);
		var prefix = opening < 0 ? "/// " : existing[..opening];
		var ending = existing.EndsWith('\r') ? "\r" : string.Empty;

		return $"{prefix}<param name=\"{name}\"></param>{ending}";
	}

	private static bool Mentions(string line, string name) =>
		line.Contains("<param", StringComparison.Ordinal)
			&& line.Contains($"name=\"{name}\"", StringComparison.Ordinal);

	private static bool Closes(string line) =>
		line.Contains("</param>", StringComparison.Ordinal) || line.Contains("/>", StringComparison.Ordinal);
}
