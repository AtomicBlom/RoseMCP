using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace RoseMcp.Worker;

/// <summary>
/// What a repository's attributes say about how git writes a file's lines into the working tree.
/// <para>
/// A repository can settle its line endings here rather than in .editorconfig, and a Windows team
/// usually does: <c>* text=auto eol=crlf</c> is how a checkout's endings stop depending on each
/// machine's <c>core.autocrlf</c>. A write that asks only .editorconfig puts LF lines into a
/// repository whose every checkout is CRLF, and git answers with a warning that it will replace them
/// the next time it touches the file -- until then the working tree holds both.
/// </para>
/// <para>
/// Read from the files rather than asked of git, the way <see cref="GitDirectory"/> reads git's own
/// directory. An edit asks this about every file it writes, a process per file is a poor price for
/// three attributes, and git need not be on the worker's path at all. What those three need is the
/// pattern rules and the order of precedence, and both are here.
/// </para>
/// <para>
/// Only an <c>eol</c> is an answer. <c>text</c> or <c>text=auto</c> without one leaves the ending to
/// each machine's <c>core.eol</c> and <c>core.autocrlf</c>, and <c>-text</c> leaves a file as it was
/// committed; either way the file's own endings already say what the machine did with it, so the
/// question passes to them. The machine's own attribute files are left out for the same reason: what
/// they did to a checkout is in its files.
/// </para>
/// </summary>
public static class GitAttributes
{
	private const string FileName = ".gitattributes";

	/// <summary>What a line defining a macro begins with, in place of a pattern.</summary>
	private const string MacroPrefix = "[attr]";

	/// <summary>The one macro git defines itself.</summary>
	private static readonly IReadOnlyList<Assignment> Binary =
	[
		new("diff", State.Unset),
		new("merge", State.Unset),
		new("text", State.Unset),
	];

	/// <summary>
	/// Whether git folds case when it matches a pattern. It does under core.ignorecase, which clone and
	/// init set on a file system that ignores case -- Windows's and macOS's -- and leave off elsewhere.
	/// </summary>
	private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

	/// <summary>
	/// The line ending git gives <paramref name="path"/> when it checks the file out, or null where the
	/// attributes leave that to the machine or to the file as it was committed, and for a path in no
	/// repository at all.
	/// </summary>
	public static string? LineEndingFor(string path)
	{
		var full = Path.GetFullPath(path);

		if (Path.GetDirectoryName(full) is not { } directory) return null;
		if (GitDirectory.Find(directory) is not { } git) return null;

		var files = Files(git, directory);
		var macros = new Dictionary<string, IReadOnlyList<Assignment>>(StringComparer.Ordinal) { ["binary"] = Binary };

		// Git reads every definition before it matches a pattern, so a macro applies to each line that
		// names it wherever that line is.
		foreach (var macro in files.SelectMany(file => file.Macros)) macros[macro.Name] = macro.Assignments;

		var state = new Dictionary<string, Assignment>(StringComparer.Ordinal);
		var name = Folded(Path.GetFileName(full));

		foreach (var file in files)
		{
			var relative = Folded(Path.GetRelativePath(file.Directory, full).Replace('\\', '/'));

			foreach (var rule in file.Rules)
			{
				if (rule.Pattern.IsMatch(rule.BaseName ? name : relative)) Assign(state, rule.Assignments, macros, []);
			}
		}

		return Ending(state);
	}

	/// <summary>
	/// What <c>text</c>, <c>eol</c> and <c>crlf</c> come to, the way git's own conversion reads them: an
	/// <c>eol</c> decides unless the file is not text at all, and <c>crlf=input</c>, which is older than
	/// <c>eol</c>, means LF.
	/// </summary>
	private static string? Ending(IReadOnlyDictionary<string, Assignment> state)
	{
		var text = TextOf(state, "text");
		if (text == Text.Undefined) text = TextOf(state, "crlf");

		if (text == Text.Binary) return null;

		var eol = state.TryGetValue("eol", out var assignment) && assignment.State == State.Valued ? assignment.Value : null;

		return eol switch
		{
			"crlf" => Whitespace.Crlf,
			"lf" => Whitespace.Lf,
			_ => text == Text.Input ? Whitespace.Lf : null,
		};
	}

	private static Text TextOf(IReadOnlyDictionary<string, Assignment> state, string name)
	{
		if (!state.TryGetValue(name, out var assignment)) return Text.Undefined;

		return assignment.State switch
		{
			State.Set => Text.Converted,
			State.Unset => Text.Binary,
			State.Valued when assignment.Value == "auto" => Text.Auto,
			State.Valued when assignment.Value == "input" => Text.Input,
			_ => Text.Undefined,
		};
	}

	/// <summary>
	/// Applies one line's attributes in order, each overriding what came before it.
	/// <para>
	/// A macro set on a path sets what it stands for in the place it was named, so an attribute written
	/// after it on the same line still has the last word, which is what git's own reading from the right
	/// comes to. A macro that reaches itself through another is expanded once.
	/// </para>
	/// </summary>
	private static void Assign(
		Dictionary<string, Assignment> state,
		IReadOnlyList<Assignment> assignments,
		IReadOnlyDictionary<string, IReadOnlyList<Assignment>> macros,
		ImmutableHashSet<string> expanding)
	{
		foreach (var assignment in assignments)
		{
			state[assignment.Name] = assignment;

			if (assignment.State != State.Set || expanding.Contains(assignment.Name)) continue;
			if (!macros.TryGetValue(assignment.Name, out var expansion)) continue;

			Assign(state, expansion, macros, expanding.Add(assignment.Name));
		}
	}

	/// <summary>
	/// Every attributes file that speaks for a file in <paramref name="directory"/>, from the least
	/// precedence to the most: the top-level <c>.gitattributes</c>, one per directory down to the
	/// file's own, then <c>info/attributes</c>.
	/// </summary>
	private static IReadOnlyList<AttributesFile> Files(GitDirectory git, string directory)
	{
		var chain = new List<string>();

		for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent)
		{
			chain.Add(at.FullName);

			if (Same(at.FullName, git.WorkTree)) break;
		}

		// The git directory was found by walking up from here, so the work tree is always on the way.
		if (!Same(chain[^1], git.WorkTree)) return [];

		chain.Reverse();

		var files = new List<AttributesFile>();

		foreach (var at in chain)
		{
			if (Read(Path.Combine(at, FileName), at, definesMacros: Same(at, git.WorkTree)) is { } file) files.Add(file);
		}

		var info = Read(Path.Combine(git.CommonDirectory(), "info", "attributes"), git.WorkTree, definesMacros: true);

		if (info is not null) files.Add(info);

		return files;
	}

	/// <summary>
	/// One attributes file's rules, and the macros it defines where it is entitled to: git takes a
	/// definition only from the top-level file and <c>info/attributes</c>, and ignores one anywhere
	/// else. Null where there is no file to read.
	/// </summary>
	private static AttributesFile? Read(string path, string directory, bool definesMacros)
	{
		string[] lines;

		try
		{
			if (!File.Exists(path)) return null;

			lines = File.ReadAllLines(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		var rules = new List<Rule>();
		var macros = new List<Macro>();

		foreach (var line in lines)
		{
			var trimmed = line.TrimStart();
			if (trimmed.Length == 0 || trimmed[0] == '#') continue;

			if (Split(trimmed) is not { } split) continue;

			var assignments = Assignments(split.Attributes);

			if (split.Pattern.StartsWith(MacroPrefix, StringComparison.Ordinal))
			{
				if (definesMacros) macros.Add(new Macro(split.Pattern[MacroPrefix.Length..], assignments));

				continue;
			}

			// Git refuses a negative pattern in an attributes file and skips the line.
			if (split.Pattern.StartsWith('!')) continue;

			if (Compile(split.Pattern) is { } pattern)
			{
				rules.Add(new Rule(pattern, !split.Pattern.Contains('/'), assignments));
			}
		}

		return new AttributesFile(directory, rules, macros);
	}

	/// <summary>
	/// The pattern a line begins with and the attributes after it. A pattern may be quoted, the way git
	/// quotes a path holding a space, with backslash escapes inside the quotes; an octal escape, or a
	/// quote that never closes, is a line this cannot read, and it contributes nothing.
	/// </summary>
	private static (string Pattern, string Attributes)? Split(string line)
	{
		if (line[0] != '"')
		{
			var end = line.IndexOfAny([' ', '\t']);

			return end < 0 ? (line, string.Empty) : (line[..end], line[end..]);
		}

		var pattern = new StringBuilder();

		for (var index = 1; index < line.Length; index++)
		{
			var character = line[index];

			if (character == '"') return (pattern.ToString(), line[(index + 1)..]);

			if (character == '\\' && index + 1 < line.Length)
			{
				index++;

				if (line[index] is >= '0' and <= '7') return null;

				pattern.Append(line[index] switch
				{
					't' => '\t',
					'n' => '\n',
					'r' => '\r',
					var other => other,
				});

				continue;
			}

			pattern.Append(character);
		}

		return null;
	}

	/// <summary>
	/// The attributes a line sets: a name alone sets one, a leading minus unsets it, a leading bang
	/// returns it to unspecified, and <c>name=value</c> gives it a value.
	/// </summary>
	private static IReadOnlyList<Assignment> Assignments(string text)
	{
		var assignments = new List<Assignment>();

		foreach (var token in text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
		{
			var equals = token.IndexOf('=');

			var assignment = token[0] switch
			{
				'-' => new Assignment(token[1..], State.Unset),
				'!' => new Assignment(token[1..], State.Unspecified),
				_ when equals > 0 => new Assignment(token[..equals], State.Valued, token[(equals + 1)..]),
				_ => new Assignment(token, State.Set),
			};

			if (assignment.Name.Length > 0) assignments.Add(assignment);
		}

		return assignments;
	}

	/// <summary>
	/// A pattern as a regular expression over what it is matched against, or null for a pattern that can
	/// match no file: one naming a directory, since attributes are not inherited by what a directory
	/// holds, and one whose bracket expression this cannot read, which is left to match nothing rather
	/// than guessed at.
	/// <para>
	/// The rules are gitignore's without negation. A star or a question mark stops at a slash. Two stars
	/// between slashes, or leading or ending the pattern next to one, cross any number of directories;
	/// anywhere else they are ordinary stars. A backslash makes the character after it literal. A leading
	/// slash anchors the pattern to its attributes file's directory, which a slash anywhere else in it
	/// does as well.
	/// </para>
	/// </summary>
	private static Regex? Compile(string pattern)
	{
		if (pattern.Length == 0 || pattern.EndsWith('/')) return null;

		var body = pattern.StartsWith('/') ? pattern[1..] : pattern;
		var regex = new StringBuilder("^");

		for (var index = 0; index < body.Length; index++)
		{
			var character = body[index];

			if (character == '*')
			{
				var end = index;
				while (end < body.Length && body[end] == '*') end++;

				var opens = index == 0 || body[index - 1] == '/';
				var closes = end == body.Length || body[end] == '/';
				var crosses = end - index > 1 && opens && closes;

				if (crosses && end == body.Length)
				{
					regex.Append(".*");
				}
				else if (crosses)
				{
					// The slash after the stars belongs to them: "**/" is any number of directories, none included.
					regex.Append("(?:.*/)?");
					end++;
				}
				else
				{
					regex.Append("[^/]*");
				}

				index = end - 1;
				continue;
			}

			if (character == '?')
			{
				regex.Append("[^/]");
				continue;
			}

			if (character == '[')
			{
				if (Bracket(body, index) is not { } bracket) return null;

				regex.Append(bracket.Regex);
				index = bracket.End;
				continue;
			}

			// Git compares an escaped character as it was written, so under core.ignorecase, where the path is
			// folded, an escaped capital matches nothing.
			if (character == '\\' && index + 1 < body.Length)
			{
				regex.Append(Regex.Escape(body[++index].ToString()));
				continue;
			}

			regex.Append(Regex.Escape(Folded(character).ToString()));
		}

		regex.Append('$');

		// Case-sensitive over the folded path, which is how git compares under core.ignorecase as well as
		// without it. Anything a regular expression still refuses is a pattern git would not match either.
		try
		{
			return new Regex(regex.ToString(), RegexOptions.CultureInvariant);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>
	/// The bracket expression opening at <paramref name="start"/> as a regular expression over the folded
	/// path, with the index of its closing bracket -- or null for one that never closes, or that uses a
	/// named class like <c>[:alpha:]</c>. A bracket expression never matches a slash.
	/// <para>
	/// Under core.ignorecase git folds the path but not the members of the set, and gives a range a second
	/// chance with the path's capital. So a capital written alone matches nothing, while a range of
	/// capitals reaches both cases: <c>[AB].cs</c> names no file there and <c>[A-Z].cs</c> names
	/// <c>b.cs</c>. Read as git reads it, or this would put a file under an <c>eol</c> that git does not.
	/// </para>
	/// </summary>
	private static (string Regex, int End)? Bracket(string pattern, int start)
	{
		var index = start + 1;
		var negated = index < pattern.Length && pattern[index] is '!' or '^';

		if (negated) index++;

		var members = new List<(char Low, char High, bool Range)>();
		var first = true;

		for (; index < pattern.Length; index++)
		{
			var character = pattern[index];

			// A closing bracket straight after the opening one is a member of the set rather than its end.
			if (character == ']' && !first) return (Set(members, negated), index);

			first = false;

			if (character == '[' && index + 1 < pattern.Length && pattern[index + 1] == ':') return null;

			if (character == '\\')
			{
				if (++index >= pattern.Length) return null;

				character = pattern[index];
			}

			// A dash between two members makes a range of them; before the closing bracket it is a dash.
			var ranged = index + 2 < pattern.Length && pattern[index + 1] == '-' && pattern[index + 2] != ']';

			if (!ranged)
			{
				members.Add((character, character, false));
				continue;
			}

			index += 2;
			var high = pattern[index];

			if (high == '\\')
			{
				if (++index >= pattern.Length) return null;

				high = pattern[index];
			}

			members.Add((character, high, true));
		}

		return null;
	}

	/// <summary>
	/// A bracket expression's members as a character class. A range written backwards contains nothing,
	/// as git reads it, and a set left with no members matches nothing -- or, negated, anything but a
	/// slash.
	/// </summary>
	private static string Set(IReadOnlyList<(char Low, char High, bool Range)> members, bool negated)
	{
		var set = new StringBuilder();

		foreach (var (low, high, range) in members)
		{
			if (low > high) continue;

			set.Append(Member(low, high));

			if (!IgnoreCase || !range || low > 'Z' || high < 'A') continue;

			var from = (char)(Math.Max(low, 'A') + ('a' - 'A'));
			var to = (char)(Math.Min(high, 'Z') + ('a' - 'A'));

			set.Append(Member(from, to));
		}

		if (set.Length == 0) return negated ? "[^/]" : "(?!)";

		return negated ? $"[^/{set}]" : $"[{set}]";
	}

	/// <summary>One member of a character class, escaped wherever a regular expression would read syntax into it.</summary>
	private static string Member(char low, char high)
	{
		static string Escaped(char character) =>
			character is '\\' or ']' or '[' or '^' or '-' ? $"\\{character}" : character.ToString();

		return low == high ? Escaped(low) : $"{Escaped(low)}-{Escaped(high)}";
	}

	/// <summary>
	/// Text as git compares it: under core.ignorecase with ASCII's capitals folded to lower case, which is
	/// the only folding git does, and as it is otherwise.
	/// </summary>
	private static string Folded(string text) =>
		IgnoreCase ? string.Concat(text.Select(Folded)) : text;

	private static char Folded(char character) =>
		IgnoreCase && character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;

	private static bool Same(string left, string right) =>
		string.Equals(
			Path.TrimEndingDirectorySeparator(left),
			Path.TrimEndingDirectorySeparator(right),
			StringComparison.OrdinalIgnoreCase);

	/// <summary>One attributes file: the directory its patterns are relative to, its rules in order, and the macros it defines.</summary>
	private sealed record AttributesFile(string Directory, IReadOnlyList<Rule> Rules, IReadOnlyList<Macro> Macros);

	/// <summary>
	/// One line: what it matches, whether it matches the file's name rather than its path, and what it
	/// sets. A pattern with no slash matches a file of that name at any depth below its attributes file.
	/// </summary>
	private sealed record Rule(Regex Pattern, bool BaseName, IReadOnlyList<Assignment> Assignments);

	private sealed record Macro(string Name, IReadOnlyList<Assignment> Assignments);

	/// <summary>One attribute as a line leaves it.</summary>
	private readonly record struct Assignment(string Name, State State, string? Value = null);

	/// <summary>
	/// The four states git gives an attribute: set, unset with a minus, back to unspecified with a bang,
	/// or given a value.
	/// </summary>
	private enum State
	{
		Set,
		Unset,
		Unspecified,
		Valued,
	}

	/// <summary>What the <c>text</c> attribute, or the older <c>crlf</c>, says about converting a file's endings at all.</summary>
	private enum Text
	{
		Undefined,
		Converted,
		Binary,
		Auto,
		Input,
	}
}
