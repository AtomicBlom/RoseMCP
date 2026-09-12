namespace RoseMcp.Symbols;

/// <summary>
/// How well a method matches what somebody typed, for an autocomplete over a target's loaded
/// modules.
/// <para>
/// Pure and separate from the search so the ranking can be tested against names rather than against
/// a real assembly. The ordering is the whole value of the feature -- a list that contains the
/// method and puts it fortieth is a list nobody scrolls to the end of -- and it is exactly the part
/// a test over a module cannot pin down, since what it should rank first depends on what was typed
/// rather than on what is in the file.
/// </para>
/// </summary>
public static class MethodQuery
{
	/// <summary>The shortest query worth searching for, below which every module matches everything.</summary>
	public const int ShortestQuery = 2;

	/// <summary>
	/// Where a method sorts against a query, lower being better, or null when it does not match.
	/// <para>
	/// The query is dot-separated needles matched in order as case-insensitive substrings of
	/// <c>Type.Method</c>, so <c>Widget.Ref</c> finds <c>MyApp.Widget.Refresh</c> and a bare
	/// <c>MyApp</c> finds everything under it. Substrings rather than prefixes because the type name
	/// a person remembers is rarely the namespace it starts with.
	/// </para>
	/// <para>
	/// What the rank adds is that matching the method name beats matching its type. Somebody typing
	/// <c>Refresh</c> means the methods called that, and a namespace containing those letters would
	/// otherwise bury them under every method of every type in it.
	/// </para>
	/// </summary>
	public static int? Rank(string typeName, string methodName, string? query)
	{
		var needles = Needles(query);
		if (needles.Count == 0) return null;

		var full = typeName + "." + methodName;

		var at = 0;
		foreach (var needle in needles)
		{
			var found = full.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
			if (found < 0) return null;

			at = found + needle.Length;
		}

		// The last needle is the one a person is still typing, so it decides the rank.
		var last = needles[^1];

		if (methodName.Equals(last, StringComparison.OrdinalIgnoreCase)) return 0;
		if (methodName.StartsWith(last, StringComparison.OrdinalIgnoreCase)) return 1;
		if (methodName.Contains(last, StringComparison.OrdinalIgnoreCase)) return 2;

		return 3;
	}

	/// <summary>
	/// Whether a query is worth running at all. One character matches most of a framework, and the
	/// answer costs a walk of every loaded module's metadata to produce.
	/// </summary>
	public static bool IsWorthSearching(string? query) =>
		Needles(query) is { Count: > 0 } needles && needles.Sum(needle => needle.Length) >= ShortestQuery;

	/// <summary>
	/// The pieces of a query, in order. A dot separates them because that is how a person writes a
	/// qualified name, and an empty piece is dropped so a trailing dot means the same as no dot --
	/// which is what somebody who has just typed <c>Widget.</c> expects.
	/// </summary>
	private static IReadOnlyList<string> Needles(string? query) => query is null
		? []
		: [.. query.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
