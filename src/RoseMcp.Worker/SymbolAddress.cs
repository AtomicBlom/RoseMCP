using System.Text;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// A name a caller can write, taken apart into what it actually constrains.
/// <para>
/// Callers address code by name rather than by line and column because a position has to be found
/// by reading the file first and goes stale the moment any earlier edit lands -- which is exactly
/// how a text edit path produces an anchor that is not found, or worse, one found in the wrong
/// place. A name survives every edit that does not rename the thing.
/// </para>
/// <para>
/// Everything about it is optional except the last segment. <c>ReadEventsAsync</c>,
/// <c>LiveAppSession.ReadEventsAsync</c> and <c>RoseMcp.Broker.LiveAppSession.ReadEventsAsync</c>
/// all name the same member, and the shortest one that is unambiguous is the one a caller has to
/// hand. A trailing parameter list separates overloads, and only then, so <c>Write(string)</c>
/// constrains what <c>Write</c> does not.
/// </para>
/// </summary>
public sealed record SymbolAddress
{
	private const string Global = "global::";

	/// <summary>What the caller wrote, for repeating back in an error.</summary>
	public required string Requested { get; init; }

	/// <summary>The last segment: the name the symbol itself carries.</summary>
	public required string Name { get; init; }

	/// <summary>
	/// Every segment, outermost first, with type arguments stripped. Matched as a suffix of the
	/// symbol's own path, so a caller may qualify as much or as little as they need to.
	/// </summary>
	public required IReadOnlyList<string> Path { get; init; }

	/// <summary>
	/// Parameter types as written, or null when none were given. An empty list is not the same as
	/// null: <c>Close()</c> asks for the overload taking nothing, while <c>Close</c> asks for
	/// whichever one there is and refuses if there are two.
	/// </summary>
	public IReadOnlyList<string>? Parameters { get; init; }

	public static SymbolAddress Parse(string requested)
	{
		var text = (requested ?? string.Empty).Trim();

		if (text.Length == 0)
		{
			throw new ArgumentException("Name the symbol to write, for example Namespace.Type.Member.");
		}

		if (text.StartsWith(Global, StringComparison.Ordinal)) text = text[Global.Length..];

		var (head, parameters) = SplitOffParameters(text);
		var path = Segments(head);

		if (path.Count == 0)
		{
			throw new ArgumentException($"'{requested}' names no symbol. Write it as Namespace.Type.Member.");
		}

		return new SymbolAddress
		{
			Requested = requested!.Trim(),
			Name = path[^1],
			Path = path,
			Parameters = parameters,
		};
	}

	/// <summary>True when <paramref name="symbol"/> is one this address could be naming.</summary>
	public bool Matches(ISymbol symbol) =>
		string.Equals(symbol.Name, Name, StringComparison.Ordinal)
			&& QualificationMatches(symbol)
			&& ParametersMatch(symbol);

	/// <summary>
	/// The symbol's own path, outermost first, as this address spells one: names only, so neither
	/// generic arity nor type arguments have to be guessed at by the caller.
	/// </summary>
	public static IReadOnlyList<string> PathOf(ISymbol symbol)
	{
		var segments = new List<string>();

		for (ISymbol? current = symbol; current is { Name.Length: > 0 }; current = Containing(current))
		{
			segments.Insert(0, current.Name);
		}

		return segments;
	}

	private static ISymbol? Containing(ISymbol symbol) =>
		(ISymbol?)symbol.ContainingType ?? symbol.ContainingNamespace;

	private bool QualificationMatches(ISymbol symbol)
	{
		var actual = PathOf(symbol);
		if (Path.Count > actual.Count) return false;

		for (var index = 1; index <= Path.Count; index++)
		{
			if (!string.Equals(Path[^index], actual[^index], StringComparison.Ordinal)) return false;
		}

		return true;
	}

	private bool ParametersMatch(ISymbol symbol)
	{
		if (Parameters is null) return true;

		var parameters = symbol switch
		{
			IMethodSymbol method => method.Parameters,
			IPropertySymbol property => property.Parameters,
			_ => [],
		};

		if (parameters.Length != Parameters.Count) return false;

		return parameters
			.Zip(Parameters)
			.All(pair => TypeMatches(pair.First.Type, pair.Second));
	}

	/// <summary>
	/// Whether a parameter type as written names this one. Both the language's spelling and the
	/// framework's are accepted, qualified or not, because a caller reading a signature back from
	/// one tool and passing it to another should not have to know which of the two it was given.
	/// </summary>
	private static bool TypeMatches(ITypeSymbol type, string requested)
	{
		var wanted = Normalise(requested);

		string[] spellings =
		[
			type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
			type.Name,
		];

		return spellings
			.Select(Normalise)
			.Any(spelling => spelling == wanted || spelling.EndsWith($".{wanted}", StringComparison.Ordinal));
	}

	private static string Normalise(string type) =>
		type.Replace(" ", string.Empty, StringComparison.Ordinal)
			.Replace(Global, string.Empty, StringComparison.Ordinal);

	/// <summary>
	/// Splits a trailing parameter list off the name, matching from the right so a parameter that is
	/// itself generic or a function type does not end the list early.
	/// </summary>
	private static (string Head, IReadOnlyList<string>? Parameters) SplitOffParameters(string text)
	{
		if (!text.EndsWith(')')) return (text, null);

		var depth = 0;

		for (var index = text.Length - 1; index >= 0; index--)
		{
			if (text[index] == ')')
			{
				depth++;
				continue;
			}

			if (text[index] != '(') continue;

			depth--;
			if (depth > 0) continue;

			return (text[..index].TrimEnd(), SplitTopLevel(text[(index + 1)..^1]));
		}

		throw new ArgumentException($"'{text}' closes a parameter list it never opens.");
	}

	/// <summary>Commas that separate parameters, which are the ones no bracket encloses.</summary>
	private static IReadOnlyList<string> SplitTopLevel(string inside)
	{
		var parts = new List<string>();
		var depth = 0;
		var start = 0;

		for (var index = 0; index < inside.Length; index++)
		{
			switch (inside[index])
			{
				case '<' or '(' or '[':
					depth++;
					break;

				case '>' or ')' or ']':
					depth--;
					break;

				case ',' when depth == 0:
					parts.Add(inside[start..index]);
					start = index + 1;
					break;
			}
		}

		parts.Add(inside[start..]);

		return [.. parts.Select(part => part.Trim()).Where(part => part.Length > 0)];
	}

	/// <summary>
	/// The dotted segments, with type arguments dropped. <c>Cache&lt;string&gt;.Add</c> and
	/// <c>Cache.Add</c> name the same member, and only one of them can be written without knowing
	/// how the declaration spells its type parameters.
	/// </summary>
	private static IReadOnlyList<string> Segments(string head)
	{
		var builder = new StringBuilder(head.Length);
		var depth = 0;

		foreach (var character in head)
		{
			switch (character)
			{
				case '<':
					depth++;
					break;

				case '>':
					depth--;
					break;

				default:
					if (depth == 0) builder.Append(character);
					break;
			}
		}

		return
		[
			.. builder.ToString()
				.Split('.')
				.Select(segment => segment.Trim())
				.Where(segment => segment.Length > 0),
		];
	}
}
