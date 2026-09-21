using System.Text;

namespace RoseMcp.Contracts;

/// <summary>
/// What resolving one placeholder produced: the value as it renders, or why it could not be read.
/// Exactly one of <see cref="Value"/> and <see cref="Error"/> is set.
/// </summary>
public readonly record struct LogValue
{
	private LogValue(string? value, string? typeName, string? error)
	{
		Value = value;
		TypeName = typeName;
		Error = error;
	}

	/// <summary>The rendered value, when it resolved.</summary>
	public string? Value { get; }

	/// <summary>What type it was, when it resolved and the type could be named.</summary>
	public string? TypeName { get; }

	/// <summary>Why it did not resolve, when it did not.</summary>
	public string? Error { get; }

	/// <summary>A value that resolved. A reader that produced no text at all reports as unreadable.</summary>
	public static LogValue Read(string? value, string? typeName) => new(value ?? "(unreadable)", typeName, null);

	/// <summary>A value that did not resolve, and what stopped it.</summary>
	public static LogValue Unavailable(string reason) => new(null, null, reason);
}

/// <summary>One piece of a log message: literal text, or a value to interpolate.</summary>
public sealed record LogMessageSegment
{
	/// <summary>The text to emit as-is, for a literal segment; null for a placeholder.</summary>
	public string? Literal { get; init; }

	/// <summary>The placeholder as it was written, for reporting what failed; null for a literal.</summary>
	public string? Expression { get; init; }

	/// <summary>The parsed path a placeholder resolves, so a hit does not re-parse it; null for a literal.</summary>
	public ValuePath? Path { get; init; }
}

/// <summary>
/// A rendered log message: the line a person reads, and the same values as data.
/// <para>
/// Both, rather than either. The line is what makes a hit legible in a stream of them, and the data
/// is what survives a client that truncates a long page -- one event can be asked for on its own and
/// its values read field by field, which a value that exists only inside a sentence cannot be.
/// </para>
/// </summary>
public sealed record LogMessageRender(string Text, IReadOnlyList<LiveVariable> Values);

/// <summary>
/// A tracepoint's log message and the values it interpolates: literal text, and <c>{path}</c>
/// placeholders where <c>path</c> is a <see cref="ValuePath"/> resolved against the hit frame's
/// arguments and locals. <c>{{</c> and <c>}}</c> are literal braces. A placeholder reads memory and
/// runs none of the debuggee's code, the same as an evaluation, so a tracepoint on a hot method
/// cannot hang the target however many values it logs.
/// <para>
/// The template is parsed when the tracepoint is added and rendered on each hit, which is what makes
/// a mistyped path an error the caller sees at the call that made the mistake rather than a line of
/// noise per hit thereafter. A path that parses but resolves to nothing is the other case entirely --
/// a local goes in and out of scope within one method -- and renders inline as
/// <c>&lt;path: reason&gt;</c>, because the hit did happen and an empty gap where a value should be
/// reads as the value having been empty.
/// </para>
/// <para>
/// Here rather than beside the host that renders it, because the host is <c>net10.0-windows</c> and
/// neither test project takes a compile reference on it. Brace escaping and the boundary between a
/// literal and a placeholder are pure string work, and getting either wrong produces a plausible
/// message rather than a failure -- the worst outcome for something whose whole job is to report
/// what a running program is doing.
/// </para>
/// </summary>
public sealed record LogMessageTemplate
{
	/// <summary>What an interpolated value's <see cref="LiveVariable.Kind"/> says it is.</summary>
	public const string LoggedKind = "logged";

	private LogMessageTemplate(IReadOnlyList<LogMessageSegment> segments, bool interpolates)
	{
		Segments = segments;
		Interpolates = interpolates;
	}

	/// <summary>The message in order: literals and placeholders as they were written.</summary>
	public IReadOnlyList<LogMessageSegment> Segments { get; }

	/// <summary>
	/// Whether anything has to be resolved at a hit. A message of pure literal text renders to itself,
	/// and a caller that knows so can skip reading the frame at all.
	/// </summary>
	public bool Interpolates { get; }

	/// <summary>
	/// Reads a message, refusing one it cannot read rather than logging the malformed text. A stray
	/// brace almost always means an interpolation that was meant and will not happen, and a tracepoint
	/// that silently logs the placeholder forever is a wasted debugging cycle.
	/// </summary>
	/// <returns>The parsed template, or null when there is no message.</returns>
	/// <exception cref="ArgumentException">A brace is unmatched, or a placeholder is not a value path.</exception>
	public static LogMessageTemplate? Parse(string? message)
	{
		if (string.IsNullOrEmpty(message)) return null;

		var segments = new List<LogMessageSegment>();
		var literal = new StringBuilder();
		var interpolates = false;
		var at = 0;

		while (at < message.Length)
		{
			var character = message[at];

			if (character == '}')
			{
				if (at + 1 < message.Length && message[at + 1] == '}')
				{
					literal.Append('}');
					at += 2;
					continue;
				}

				throw new ArgumentException(
					$"'{message}' has a closing brace with nothing opened before it. Write }}}} for a literal one.");
			}

			if (character != '{')
			{
				literal.Append(character);
				at++;
				continue;
			}

			if (at + 1 < message.Length && message[at + 1] == '{')
			{
				literal.Append('{');
				at += 2;
				continue;
			}

			var close = message.IndexOf('}', at + 1);
			if (close < 0)
			{
				throw new ArgumentException(
					$"'{message}' has an opening brace that is never closed. Write {{{{ for a literal one.");
			}

			var expression = message[(at + 1)..close].Trim();
			if (expression.Length == 0)
			{
				throw new ArgumentException(
					$"'{message}' has an empty placeholder. Name what to log, such as {{count}} or {{state.Inner.Count}}.");
			}

			if (literal.Length > 0)
			{
				segments.Add(new LogMessageSegment { Literal = literal.ToString() });
				literal.Clear();
			}

			segments.Add(new LogMessageSegment { Expression = expression, Path = ValuePath.Parse(expression) });
			interpolates = true;
			at = close + 1;
		}

		if (literal.Length > 0) segments.Add(new LogMessageSegment { Literal = literal.ToString() });

		return new LogMessageTemplate(segments, interpolates);
	}

	/// <summary>
	/// The message for one hit, with each placeholder replaced by what <paramref name="resolve"/>
	/// made of it, and each placeholder also carried out as a value of its own.
	/// <para>
	/// A placeholder that did not resolve keeps its text in the line, in angle brackets with the
	/// reason, so the line says which value is missing rather than reading as a shorter message. It
	/// is carried out the same way, because a value that is not there is a fact about the hit, and
	/// dropping it would leave a reader counting positions to work out which one went.
	/// </para>
	/// </summary>
	public LogMessageRender Render(Func<ValuePath, LogValue> resolve)
	{
		var rendered = new StringBuilder();
		var values = new List<LiveVariable>();

		foreach (var segment in Segments)
		{
			if (segment.Literal is { } literal)
			{
				rendered.Append(literal);
				continue;
			}

			var expression = segment.Expression!;
			var value = resolve(segment.Path!);
			var text = value.Error is { } error ? $"<{expression}: {error}>" : value.Value;

			rendered.Append(text);
			values.Add(new LiveVariable
			{
				Name = expression,
				Kind = LoggedKind,
				TypeName = value.TypeName,
				Value = text,
				Path = expression,
				HasChildren = false,
			});
		}

		return new LogMessageRender(rendered.ToString(), values);
	}
}
