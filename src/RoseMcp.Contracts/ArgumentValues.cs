namespace RoseMcp.Contracts;

/// <summary>
/// The arguments that are an enum wearing a string, and the one refusal they all share.
/// <para>
/// MCP carries these as text, and the tempting shape is a switch whose default is the common case.
/// Four of them were written that way, and each turned a typo into a confident answer to a
/// different question: <c>scope: "proj"</c> analysed the whole solution and returned diagnostics
/// for fourteen projects when one was asked about; <c>minimumSeverity: "warn"</c> reported
/// warnings when errors were wanted; a misspelt event kind was dropped from the filter, and a
/// filter that lost every name widened the answer instead of narrowing it; and any step mode but
/// <c>in</c> or <c>out</c> stepped over -- moving the target somewhere nobody asked and reporting
/// success.
/// </para>
/// <para>
/// So the default is a refusal that lists what is allowed, which is the shape
/// <c>rose_apply_code_fix</c> already had and the four arguments added with the write tools already
/// follow. Here rather than beside each tool so the sentence is one sentence, and so a test can
/// reach the parses without a host.
/// </para>
/// </summary>
public static class ArgumentValues
{
	/// <summary>
	/// The refusal for an argument that takes one of a fixed set and was given something else.
	/// </summary>
	/// <param name="argument">The argument's name, as the caller spells it.</param>
	/// <param name="given">What arrived.</param>
	/// <param name="allowed">Every value that would have been accepted.</param>
	public static ArgumentException Unknown(string argument, string? given, params string[] allowed) =>
		new($"Unknown {argument} '{given}'. Use {string.Join(", ", allowed)}.");

	/// <summary>
	/// How much of the solution a read covers. <c>file</c> is accepted for <c>document</c>, since
	/// that is what the other tools call the same thing.
	/// </summary>
	/// <exception cref="ArgumentException">The scope is not one of the three.</exception>
	public static DiagnosticScope Scope(string? scope) => scope?.Trim().ToLowerInvariant() switch
	{
		null or "" => DiagnosticScope.Solution,
		"document" or "file" => DiagnosticScope.Document,
		"project" => DiagnosticScope.Project,
		"solution" => DiagnosticScope.Solution,
		_ => throw Unknown("scope", scope, "document", "project", "solution"),
	};

	/// <summary>
	/// Which direction a step goes.
	/// </summary>
	/// <exception cref="ArgumentException">The mode is not one of the three.</exception>
	public static StepDirection Step(string? mode) => mode?.Trim().ToLowerInvariant() switch
	{
		null or "" => StepDirection.Over,
		"in" => StepDirection.In,
		"over" => StepDirection.Over,
		"out" => StepDirection.Out,
		_ => throw Unknown("step mode", mode, "in", "over", "out"),
	};

	/// <summary>
	/// The event kinds a page is filtered to, or null for no filter.
	/// <para>
	/// Every name has to parse. Dropping one silently is the worst of the defaults this class exists
	/// to remove: a freshly started app produces hundreds of ModuleLoaded events, so a misspelt filter
	/// does not merely answer a different question, it answers it at a size that buries the one event
	/// the caller was waiting for.
	/// </para>
	/// <para>
	/// A list rather than comma-separated text, because the rest of the surface passes a list of
	/// anything there can be several of and one CSV among six arrays is a thing a caller has to
	/// remember rather than read. An entry carrying commas is still split, so the spelling that was
	/// there goes on working.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">One of the names is not an event kind.</exception>
	public static IReadOnlyCollection<LiveDebugEventKind>? EventKinds(IReadOnlyList<string>? kinds)
	{
		if (kinds is null || kinds.Count == 0) return null;

		var parsed = new HashSet<LiveDebugEventKind>();

		foreach (var name in kinds.SelectMany(Names))
		{
			if (!Enum.TryParse<LiveDebugEventKind>(name, ignoreCase: true, out var kind))
			{
				throw Unknown("event kind", name, [.. Enum.GetNames<LiveDebugEventKind>()]);
			}

			parsed.Add(kind);
		}

		return parsed.Count == 0 ? null : parsed;
	}

	/// <summary>The names one entry carries, which is usually one and may be a comma-separated list.</summary>
	private static IEnumerable<string> Names(string? entry) =>
		entry?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
