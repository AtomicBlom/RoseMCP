using System.Reflection.Metadata.Ecma335;

namespace RoseMcp.Symbols;

/// <summary>
/// Finds methods by name across a target's loaded modules, for choosing where to put a breakpoint
/// without an IDE to browse.
/// <para>
/// Reads module files, and never the debuggee. The list of modules is a fact the debugger holds, but
/// what is in one is on disk, so a search costs no interruption to a running target and can be run
/// while it is mid-request -- which is the only way an autocomplete is usable at all.
/// </para>
/// <para>
/// Compiler-generated methods are left out. A lambda body and an async state machine are where a
/// breakpoint inside one has to go, but they are not things anybody types the name of: they are
/// reached by picking a line in the method that was written, which is what
/// <see cref="MethodRegion"/> serves.
/// </para>
/// </summary>
public static class MethodSearch
{
	/// <summary>
	/// The best matches for a query across these modules.
	/// <para>
	/// Ranked before it is cut down, so the limit takes the top of the whole answer rather than the
	/// first modules to be read. After the rank, a module whose symbols are here comes first: that is
	/// the code the person is working on, and it is the only kind a position can be picked inside.
	/// </para>
	/// </summary>
	/// <param name="modulePaths">The module files to read, usually a target's loaded modules.</param>
	/// <param name="query">What was typed. Too short a one matches most of a framework and is refused.</param>
	/// <param name="limit">How many matches to return.</param>
	public static MethodSearchResult Search(IReadOnlyList<string> modulePaths, string? query, int limit)
	{
		if (limit < 1) throw new ArgumentException("A limit of at least one is required.", nameof(limit));

		if (!MethodQuery.IsWorthSearching(query))
		{
			return new MethodSearchResult { Matches = [], Total = 0, ModulesSearched = 0, ModulesUnreadable = 0 };
		}

		var found = new List<Hit>();
		var searched = 0;
		var unreadable = 0;

		foreach (var modulePath in modulePaths)
		{
			if (SymbolCache.Shared.For(modulePath) is not { } symbols)
			{
				unreadable++;
				continue;
			}

			searched++;
			Collect(symbols, modulePath, query, found);
		}

		found.Sort(Best);

		return new MethodSearchResult
		{
			Matches = [.. found.Take(limit).Select(Describe)],
			Total = found.Count,
			ModulesSearched = searched,
			ModulesUnreadable = unreadable,
		};
	}

	private static void Collect(ModuleSymbols symbols, string modulePath, string? query, List<Hit> found)
	{
		var module = Path.GetFileNameWithoutExtension(modulePath);
		var hasSymbols = symbols.Pdb is not null;

		try
		{
			var metadata = symbols.Metadata;

			foreach (var typeHandle in metadata.TypeDefinitions)
			{
				var type = metadata.GetTypeDefinition(typeHandle);
				var typeName = MethodTokens.FullName(metadata, type);

				// Every method of a closure holder or a state machine is compiler-generated, so the
				// whole type goes rather than each of its methods being tested.
				if (typeName.Contains('<')) continue;

				foreach (var methodHandle in type.GetMethods())
				{
					var method = metadata.GetMethodDefinition(methodHandle);
					var methodName = metadata.GetString(method.Name);

					if (methodName.StartsWith('<')) continue;
					if (MethodQuery.Rank(typeName, methodName, query) is not { } rank) continue;

					found.Add(new Hit(
						modulePath,
						module,
						typeName,
						methodName,
						MetadataTokens.GetToken(methodHandle),
						hasSymbols,
						rank));
				}
			}
		}
		catch (Exception)
		{
			// A module whose metadata will not enumerate contributes what it managed. It is one of
			// however many are loaded, and refusing the whole search over it would mean a target
			// carrying one odd assembly has no autocomplete at all.
		}
	}

	/// <summary>
	/// Which of two matches a reader wants to see first: the better rank, then the one whose symbols
	/// are here, then the shorter name, then alphabetically so the order does not move between two
	/// searches that found the same things.
	/// </summary>
	private static int Best(Hit left, Hit right)
	{
		if (left.Rank != right.Rank) return left.Rank - right.Rank;
		if (left.HasSymbols != right.HasSymbols) return left.HasSymbols ? -1 : 1;

		var byLength = Length(left) - Length(right);
		if (byLength != 0) return byLength;

		var byType = string.CompareOrdinal(left.TypeName, right.TypeName);

		return byType != 0 ? byType : string.CompareOrdinal(left.MethodName, right.MethodName);
	}

	private static int Length(Hit hit) => hit.TypeName.Length + hit.MethodName.Length;

	/// <summary>
	/// A hit written up for the caller. Done after the sort and the cut, because the display name is
	/// a parse of two strings and a broad query matches thousands of methods nobody will be shown.
	/// </summary>
	private static MethodCandidate Describe(Hit hit) => new()
	{
		Location = $"{hit.Module}!{hit.TypeName}.{hit.MethodName}",
		Module = hit.Module,
		ModulePath = hit.ModulePath,
		TypeName = hit.TypeName,
		MethodName = hit.MethodName,
		DisplayName = MethodDisplayName.Of(hit.TypeName, hit.MethodName),
		Token = hit.Token,
		HasSymbols = hit.HasSymbols,
		Rank = hit.Rank,
	};

	private readonly record struct Hit(
		string ModulePath,
		string Module,
		string TypeName,
		string MethodName,
		int Token,
		bool HasSymbols,
		int Rank);
}
