namespace RoseMcp.Symbols;

/// <summary>
/// A method's name as a person would say it, given the name metadata records.
/// <para>
/// Metadata spells a lambda <c>MyApp.Widget+&lt;&gt;c.&lt;Refresh&gt;b__3_0</c> and an async body
/// <c>MyApp.Widget+&lt;Refresh&gt;d__3.MoveNext</c>. Both are exactly where a breakpoint inside a
/// lambda or after an <c>await</c> has to go, so they are shown rather than hidden -- and shown as
/// the method they came from, because a reader who clicked a line in <c>Refresh</c> and is offered
/// <c>&lt;&gt;c.&lt;Refresh&gt;b__3_0</c> has been told they picked something else.
/// </para>
/// <para>
/// Display only. The metadata spelling is what binds a breakpoint, so a caller keeps both.
/// </para>
/// </summary>
public static class MethodDisplayName
{
	private static readonly (string Prefix, string Role)[] Accessors =
	[
		("get_", "get"),
		("set_", "set"),
		("add_", "add"),
		("remove_", "remove"),
	];

	/// <summary>How to show a method, from its declaring type's full metadata name and its own.</summary>
	public static string Of(string typeName, string methodName)
	{
		var owner = OwnerOf(typeName);

		// <Refresh>b__3_0 and <Refresh>g__Inner|3_0 both name the method they were written inside.
		if (Angled(methodName) is { } written)
		{
			if (written.Suffix.StartsWith("g__", StringComparison.Ordinal))
			{
				var bar = written.Suffix.IndexOf('|');
				var local = bar > 3 ? written.Suffix[3..bar] : written.Suffix[3..];

				return $"{owner}.{written.Host} > {local} (local function)";
			}

			return $"{owner}.{written.Host} (lambda)";
		}

		// An async method's body and an iterator's are both a MoveNext on a state machine named
		// after the method that kicked it off. Which of the two it is takes reading the interfaces
		// the state machine implements, and it changes nothing about where the breakpoint goes.
		if (StateMachineKickoff(typeName) is { } kickoff) return $"{owner}.{kickoff} (async or iterator)";

		if (methodName is ".ctor") return $"{owner} (constructor)";
		if (methodName is ".cctor") return $"{owner} (static constructor)";

		foreach (var (prefix, role) in Accessors)
		{
			if (methodName.Length > prefix.Length && methodName.StartsWith(prefix, StringComparison.Ordinal))
			{
				return $"{owner}.{methodName[prefix.Length..]} ({role})";
			}
		}

		return $"{owner}.{methodName}";
	}

	/// <summary>
	/// Whether the compiler wrote this method rather than a person.
	/// <para>
	/// Worth asking because these belong in a source-position picker and not in a name search: a
	/// list of methods to break in is a list of things somebody wrote, and the closure holders,
	/// state machines and lambda bodies beside them outnumber the real methods in async code.
	/// </para>
	/// </summary>
	public static bool IsCompilerGenerated(string typeName, string methodName) =>
		typeName.Contains('<') || methodName.StartsWith('<');

	/// <summary>
	/// The type as a person names it: no namespace, no arity tick, and no compiler-invented nesting
	/// level, so the closure class holding a lambda reads as the class the lambda was written in.
	/// </summary>
	private static string OwnerOf(string typeName)
	{
		var nested = typeName.Split('+');
		var names = new List<string>();

		for (var level = 0; level < nested.Length; level++)
		{
			var part = level == 0 ? nested[0][(nested[0].LastIndexOf('.') + 1)..] : nested[level];
			if (part.Contains('<')) continue;

			var tick = part.IndexOf('`');
			names.Add(tick < 0 ? part : part[..tick]);
		}

		return names.Count == 0 ? typeName : string.Join('.', names);
	}

	/// <summary>The method an async or iterator state machine was written as, or null if this is not one.</summary>
	private static string? StateMachineKickoff(string typeName)
	{
		foreach (var part in typeName.Split('+').Reverse())
		{
			if (Angled(part) is { } machine && machine.Suffix.StartsWith("d__", StringComparison.Ordinal))
			{
				return machine.Host;
			}
		}

		return null;
	}

	/// <summary>
	/// The name inside the leading angle brackets and whatever follows them, for the
	/// <c>&lt;Host&gt;suffix</c> shape every compiler-generated name takes. Null when the name is
	/// not one.
	/// </summary>
	private static (string Host, string Suffix)? Angled(string name)
	{
		if (!name.StartsWith('<')) return null;

		var close = name.IndexOf('>');

		return close > 1 ? (name[1..close], name[(close + 1)..]) : null;
	}
}
