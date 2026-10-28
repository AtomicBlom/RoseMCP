namespace RoseMcp.Symbols;

/// <summary>
/// Types among a target's modules whose names are close to one nobody declares, for telling a caller
/// who misspelled a type what they probably meant.
/// <para>
/// A location naming a type that no loaded module declares is accepted, since the module that does
/// may not have loaded yet, and that is indistinguishable from a typo until something says otherwise.
/// Waiting on a typo waits forever. So the binding names what is loaded and close: a different case
/// (<c>Win2D</c> for <c>Win2d</c>), a slip of a letter or two, or the right name in another namespace.
/// </para>
/// <para>
/// A suggestion, never a substitution. Binding to the near match would put a breakpoint somewhere
/// other than where it was asked for and report success, and a name a letter away from another can be
/// a different type entirely.
/// </para>
/// </summary>
public static class TypeSuggestions
{
	/// <summary>The most suggestions given, so a short name in a large target does not list half of it.</summary>
	public const int MaxSuggestions = 3;

	/// <summary>
	/// The types among <paramref name="modulePaths"/> closest to <paramref name="typeName"/>, best
	/// first, each as metadata spells it with the module it is in. Empty when nothing is close.
	/// </summary>
	/// <param name="modulePaths">The module files to read, usually a target's loaded modules.</param>
	/// <param name="typeName">The type as written, with a <c>+</c> before each nesting level.</param>
	/// <param name="assembly">When given, only modules of this simple name are read.</param>
	public static IReadOnlyList<TypeSuggestion> For(IEnumerable<string> modulePaths, string typeName, string? assembly)
	{
		var simpleName = SimpleName(typeName);
		var allowed = Allowance(simpleName.Length);
		var found = new List<(TypeSuggestion Suggestion, int Distance, int FullDistance)>();

		foreach (var modulePath in modulePaths)
		{
			if (!TypeOwners.Admits(modulePath, assembly)) continue;
			if (SymbolCache.Shared.For(modulePath)?.Metadata is not { } metadata) continue;

			try
			{
				foreach (var typeHandle in metadata.TypeDefinitions)
				{
					var type = metadata.GetTypeDefinition(typeHandle);
					var candidate = metadata.GetString(type.Name);

					// Compiler-generated types are nobody's spelling of anything.
					if (candidate.Contains('<')) continue;
					if (Math.Abs(candidate.Length - simpleName.Length) > allowed) continue;

					var distance = Distance(candidate, simpleName);
					if (distance > allowed) continue;

					var fullName = MethodTokens.FullName(metadata, type);

					// The name exactly as written is not a near match: it is the type, and the
					// question is only being asked because this module was not among those searched.
					if (fullName == typeName) continue;

					found.Add((new TypeSuggestion(fullName, modulePath), distance, Distance(fullName, typeName)));
				}
			}
			catch (Exception)
			{
				// A module whose metadata will not enumerate offers nothing, rather than costing the
				// caller the suggestions every other module had.
			}
		}

		return
		[
			.. found
				.OrderBy(entry => entry.Distance)
				.ThenBy(entry => entry.FullDistance)
				.ThenBy(entry => entry.Suggestion.TypeName, StringComparer.Ordinal)
				.Select(entry => entry.Suggestion)
				.DistinctBy(suggestion => suggestion.TypeName)
				.Take(MaxSuggestions),
		];
	}

	/// <summary>
	/// The clause a binding's detail ends with, naming the suggestions and the module each is in, or
	/// an empty string when there are none.
	/// </summary>
	public static string Clause(IReadOnlyList<TypeSuggestion> suggestions)
	{
		if (suggestions.Count == 0) return string.Empty;

		var named = suggestions.Select(suggestion => $"{suggestion.TypeName} (in {Path.GetFileName(suggestion.ModulePath)})");

		return $"; did you mean {string.Join(" or ", named)}? A misspelled name never binds";
	}

	private static string SimpleName(string typeName) => typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..];

	/// <summary>
	/// How many edits a name of this length may be from a candidate and still be suggested. A case
	/// difference costs nothing, since it is the slip a person cannot see. Short names allow nothing
	/// else, because three letters one edit apart are as often two different words as one misspelled.
	/// </summary>
	private static int Allowance(int length) => length switch
	{
		< 4 => 0,
		< 8 => 1,
		< 16 => 2,
		_ => 3,
	};

	/// <summary>The edit distance between two names, ignoring case.</summary>
	private static int Distance(string left, string right)
	{
		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];

		for (var j = 0; j <= right.Length; j++) previous[j] = j;

		for (var i = 1; i <= left.Length; i++)
		{
			current[0] = i;
			for (var j = 1; j <= right.Length; j++)
			{
				var same = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]);
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (same ? 0 : 1));
			}

			(previous, current) = (current, previous);
		}

		return previous[right.Length];
	}
}

/// <summary>A type close to one that was asked for, and the module that declares it.</summary>
/// <param name="TypeName">The type as metadata spells it.</param>
/// <param name="ModulePath">The module file that declares it.</param>
public sealed record TypeSuggestion(string TypeName, string ModulePath);
