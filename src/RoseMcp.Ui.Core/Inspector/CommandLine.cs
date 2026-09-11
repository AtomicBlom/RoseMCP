using System.Text;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Turning one command-line string back into the arguments it was built from.
/// <para>
/// Needed because a redirected activation arrives as a single raw string rather than the parsed
/// array a process start gives: the second launch of a single-instance app hands its whole command
/// line to the instance already running, and that instance has to read it the way Windows would
/// have. Doing it by splitting on spaces loses a path with a space in it, which is most of them.
/// </para>
/// </summary>
public static class CommandLine
{
	/// <summary>
	/// Splits a command line into arguments, honouring double quotes and backslash escapes the way
	/// the C runtime's own parser does.
	/// </summary>
	/// <param name="commandLine">The raw line, including the executable as its first token.</param>
	/// <param name="skipExecutable">
	/// Drop the first token, which a raw command line carries and a parsed argument array does not.
	/// </param>
	public static string[] Split(string? commandLine, bool skipExecutable = true)
	{
		var arguments = new List<string>();
		var current = new StringBuilder();
		var text = commandLine ?? string.Empty;

		var quoted = false;
		var started = false;
		var backslashes = 0;

		foreach (var character in text)
		{
			if (character == '\\')
			{
				backslashes++;
				started = true;
				continue;
			}

			if (character == '"')
			{
				// A run of backslashes before a quote is halved, and an odd one escapes the quote
				// rather than ending it. That is the rule the C runtime uses, and a path ending in a
				// separator inside quotes -- "C:\dir\" -- is what makes it matter.
				current.Append('\\', backslashes / 2);
				if (backslashes % 2 == 1) current.Append('"');
				else quoted = !quoted;

				backslashes = 0;
				started = true;
				continue;
			}

			current.Append('\\', backslashes);
			backslashes = 0;

			if (!quoted && char.IsWhiteSpace(character))
			{
				if (started) arguments.Add(current.ToString());

				current.Clear();
				started = false;
				continue;
			}

			current.Append(character);
			started = true;
		}

		current.Append('\\', backslashes);
		if (started || current.Length > 0) arguments.Add(current.ToString());

		return skipExecutable ? [.. arguments.Skip(1)] : [.. arguments];
	}
}
