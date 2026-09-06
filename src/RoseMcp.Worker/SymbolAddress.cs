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

	/// <summary>
	/// The CLR's constructor spellings, longest first so <c>..cctor</c> is not read as <c>..ctor</c>
	/// with a stray c in front of it.
	/// </summary>
	private static readonly (string Suffix, ConstructorKind Kind)[] Spellings =
	[
		("..cctor", ConstructorKind.Static),
		("..ctor", ConstructorKind.Instance),
	];

	/// <summary>What the caller wrote, for repeating back in an error.</summary>
	public required string Requested { get; init; }

	/// <summary>
	/// How an address is spelled: fully qualified, with parameter types and nothing else.
	/// <para>
	/// Deliberately not <see cref="SymbolSignature.Format"/>, which is for reading. That one leads
	/// with the return type and names the parameters, so
	/// <c>string RoseMcp.Broker.WorkspaceHints.From(string workspace, string[] paths)</c> is what a
	/// result carried -- and none of it parses back, because the space before the second qualified
	/// name lands inside a segment and a parameter's name is not its type. A caller who read a symbol
	/// out of one answer and wanted to edit it had to take the string apart by hand.
	/// </para>
	/// <para>
	/// Type parameters are omitted because <see cref="Parse"/> strips them anyway, and parameter types
	/// are minimally qualified, which is one of the spellings the match already accepts.
	/// </para>
	/// </summary>
	private static readonly SymbolDisplayFormat AddressFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.None,
		memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
		parameterOptions: SymbolDisplayParameterOptions.IncludeType,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	/// <summary>
	/// The address that names this symbol, or null where nothing can: a local, a parameter, a label or
	/// a type parameter is declared inside a member rather than as one, so there is no name a
	/// declaration search could find and reporting a bare identifier would invite a call that fails.
	/// </summary>
	public static string? Of(ISymbol symbol) => symbol.Kind switch
	{
		SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Label
			or SymbolKind.TypeParameter or SymbolKind.RangeVariable or SymbolKind.Discard => null,
		_ => symbol.ToDisplayString(AddressFormat),
	};

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

	/// <summary>
	/// Which constructor this names, if any. When it names one, <see cref="Name"/> and
	/// <see cref="Path"/> address the <em>type</em>, since that is the name the constructor is
	/// declared under and the one a declaration search can find.
	/// </summary>
	public ConstructorKind Constructor { get; init; }

	public static SymbolAddress Parse(string requested)
	{
		var text = (requested ?? string.Empty).Trim();

		if (text.Length == 0)
		{
			throw new ArgumentException("Name the symbol to write, for example Namespace.Type.Member.");
		}

		if (text.StartsWith(Global, StringComparison.Ordinal)) text = text[Global.Length..];

		var (head, parameters) = SplitOffParameters(text);
		var (typePath, constructor) = SplitOffConstructor(head, requested!);

		if (typePath.Length == 0)
		{
			throw new ArgumentException($"'{requested}' names no symbol. Write it as Namespace.Type.Member.");
		}

		return new SymbolAddress
		{
			Requested = requested!.Trim(),
			Name = typePath[^1],
			Path = typePath,
			Parameters = parameters,
			Constructor = constructor,
		};
	}

	/// <summary>True when <paramref name="symbol"/> is one this address could be naming.</summary>
	public bool Matches(ISymbol symbol)
	{
		if (Constructor != ConstructorKind.None) return ConstructorMatches(symbol);

		return string.Equals(symbol.Name, Name, StringComparison.Ordinal)
			&& QualificationMatches(symbol)
			&& ParametersMatch(symbol);
	}

	/// <summary>
	/// A constructor is matched through its containing type, because that is what carries the name
	/// the caller wrote. The symbol's own name is <c>.ctor</c>, which no address spells directly.
	/// </summary>
	private bool ConstructorMatches(ISymbol symbol)
	{
		var wanted = Constructor == ConstructorKind.Static ? MethodKind.StaticConstructor : MethodKind.Constructor;

		if (symbol is not IMethodSymbol method || method.MethodKind != wanted) return false;

		return string.Equals(method.ContainingType.Name, Name, StringComparison.Ordinal)
			&& QualificationMatches(method.ContainingType)
			&& ParametersMatch(method);
	}

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
	/// Takes a constructor spelling off the end of a name and returns the path to the type it
	/// constructs.
	/// <para>
	/// Two spellings, because both are the natural first guess from somewhere. <c>Type..ctor</c> is
	/// what the CLR calls it and what a stack trace shows; <c>Type.Type</c> is what C# writes, and
	/// it cannot mean anything else, since a member may not share the name of the type enclosing it.
	/// Accepting neither costs more than it looks: a constructor is where a parameter is added most
	/// often, and the failure is a refusal saying nothing is declared there, which reads as the name
	/// being wrong rather than as the spelling being unsupported.
	/// </para>
	/// </summary>
	private static (string[] TypePath, ConstructorKind Constructor) SplitOffConstructor(
		string head,
		string requested)
	{
		foreach (var (suffix, kind) in Spellings)
		{
			if (!head.EndsWith(suffix, StringComparison.Ordinal)) continue;

			var path = Segments(head[..^suffix.Length]);

			if (path.Length == 0)
			{
				throw new ArgumentException(
					$"'{requested}' names a constructor with no type. Write it as Namespace.Type{suffix}.");
			}

			return (path, kind);
		}

		var segments = Segments(head);

		// A member may not share the name of the type enclosing it, so a repeated last segment is a
		// constructor and cannot be read as anything else.
		var repeats = segments.Length >= 2
			&& string.Equals(segments[^1], segments[^2], StringComparison.Ordinal);

		return repeats
			? (segments[..^1], ConstructorKind.Instance)
			: (segments, ConstructorKind.None);
	}

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
	private static string[] Segments(string head)
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
