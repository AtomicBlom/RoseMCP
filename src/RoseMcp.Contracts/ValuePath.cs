namespace RoseMcp.Contracts;

/// <summary>What a value path starts from.</summary>
public enum ValuePathRoot
{
	/// <summary>An argument of the frame, by slot: <c>arg:0</c>.</summary>
	Argument,

	/// <summary>A local of the frame, by slot: <c>local:2</c>.</summary>
	Local,

	/// <summary>
	/// A name, resolved against the frame's arguments and then its locals the way an evaluation
	/// resolves one. It is what a person types and what an older event's variable carries.
	/// </summary>
	Name,
}

/// <summary>One step from a value to a value inside it: a field by name, or an element by index.</summary>
public sealed record ValuePathStep
{
	/// <summary>The field's name, or null when this step is an index.</summary>
	public string? Field { get; init; }

	/// <summary>The element's position, or null when this step is a field.</summary>
	public int? Index { get; init; }
}

/// <summary>
/// How a value inside a stopped frame is addressed: a root, then fields and indexes into it --
/// <c>arg:0.Inner.Items[3].Name</c>.
/// <para>
/// A slot is the addressing form rather than a name because a name is not always unique or even
/// present: a compiler temporary has no name, and two blocks can reuse one slot under different
/// names. A bare name is still accepted, resolved the way an evaluation resolves one, because that
/// is what a person types and what an event captured before this grammar existed carries.
/// </para>
/// <para>
/// Parsing lives here, beside <see cref="LiveVariable.Path"/> which is the only thing that produces
/// one, so the end that writes a path and the end that reads it cannot drift. The grammar is pure
/// string work with no debugger in it; resolving a parsed path against live values is the host's,
/// and cannot be tested without a target.
/// </para>
/// </summary>
public sealed record ValuePath
{
	private const string ArgumentPrefix = "arg:";
	private const string LocalPrefix = "local:";

	/// <summary>Which of the three root forms this is.</summary>
	public required ValuePathRoot Kind { get; init; }

	/// <summary>The slot, for <see cref="ValuePathRoot.Argument"/> and <see cref="ValuePathRoot.Local"/>.</summary>
	public int Slot { get; init; }

	/// <summary>The name, for <see cref="ValuePathRoot.Name"/>.</summary>
	public string? Name { get; init; }

	/// <summary>The steps into the value, in order. Empty for the root itself.</summary>
	public IReadOnlyList<ValuePathStep> Steps { get; init; } = [];

	/// <summary>The address of an argument by slot.</summary>
	public static string Argument(int slot) => $"{ArgumentPrefix}{slot}";

	/// <summary>The address of a local by slot.</summary>
	public static string Local(int slot) => $"{LocalPrefix}{slot}";

	/// <summary>The address of a field of the value at <paramref name="parent"/>.</summary>
	public static string Field(string parent, string field) => $"{parent}.{field}";

	/// <summary>The address of an element of the array at <paramref name="parent"/>.</summary>
	public static string Element(string parent, int index) => $"{parent}[{index}]";

	/// <summary>
	/// Reads a path, refusing anything it cannot read rather than guessing at it. A path that means
	/// something other than what it says would expand the wrong value and report success, which is
	/// the one outcome worth an exception here.
	/// </summary>
	/// <exception cref="ArgumentException">The path is empty or does not parse, with what failed.</exception>
	public static ValuePath Parse(string? path)
	{
		var text = path?.Trim() ?? string.Empty;
		if (text.Length == 0) throw new ArgumentException("A value path is required, such as arg:0 or local:2.Inner.");

		var rootEnd = EndOfRoot(text);
		var root = text[..rootEnd];
		var steps = ParseSteps(text, rootEnd);

		if (root.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
		{
			return new ValuePath { Kind = ValuePathRoot.Argument, Slot = Slotted(root, ArgumentPrefix), Steps = steps };
		}

		if (root.StartsWith(LocalPrefix, StringComparison.Ordinal))
		{
			return new ValuePath { Kind = ValuePathRoot.Local, Slot = Slotted(root, LocalPrefix), Steps = steps };
		}

		if (root.Length == 0 || root.Contains(':'))
		{
			throw new ArgumentException(
				$"'{text}' does not start with a value root. Use arg:N, local:N, or the name of an argument or local.");
		}

		return new ValuePath { Kind = ValuePathRoot.Name, Name = root, Steps = steps };
	}

	/// <summary>Where the root token ends: at the first step separator, or at the end of the path.</summary>
	private static int EndOfRoot(string text)
	{
		var dot = text.IndexOf('.');
		var bracket = text.IndexOf('[');

		if (dot < 0) return bracket < 0 ? text.Length : bracket;

		return bracket < 0 ? dot : Math.Min(dot, bracket);
	}

	private static int Slotted(string root, string prefix)
	{
		var digits = root[prefix.Length..];
		if (int.TryParse(digits, out var slot) && slot >= 0) return slot;

		throw new ArgumentException($"'{root}' needs a slot number after '{prefix}', such as {prefix}0.");
	}

	private static IReadOnlyList<ValuePathStep> ParseSteps(string text, int from)
	{
		var steps = new List<ValuePathStep>();
		var at = from;

		while (at < text.Length)
		{
			if (text[at] == '.')
			{
				at++;
				var end = at;
				while (end < text.Length && text[end] != '.' && text[end] != '[') end++;

				var field = text[at..end];
				if (field.Length == 0) throw new ArgumentException($"'{text}' has a '.' with no field name after it.");

				steps.Add(new ValuePathStep { Field = field });
				at = end;
				continue;
			}

			if (text[at] == '[')
			{
				var close = text.IndexOf(']', at);
				if (close < 0) throw new ArgumentException($"'{text}' has a '[' that is never closed.");

				var inside = text[(at + 1)..close];
				if (!int.TryParse(inside, out var index) || index < 0)
				{
					throw new ArgumentException($"'{text}' indexes with '{inside}', which is not a position.");
				}

				steps.Add(new ValuePathStep { Index = index });
				at = close + 1;
				continue;
			}

			throw new ArgumentException($"'{text}' has '{text[at]}' where a '.' or a '[' was expected.");
		}

		return steps;
	}
}
