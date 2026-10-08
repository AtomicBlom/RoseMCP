using System.Text.Json;

namespace RoseMcp.Contracts;

/// <summary>
/// Says which argument a caller got wrong: one sent in the wrong shape, for a call the JSON binder
/// refused, and one sent under a name the tool does not declare, for any call at all.
/// <para>
/// The binder's own account names a CLR type the caller never wrote and points at the root of the
/// document: "The JSON value could not be converted to System.String[]. Path: $". It is accurate and
/// there is nothing to act on in it -- a tool with three string arguments and one array does not say
/// which of the four was sent wrong, and the type it names is not part of the tool's vocabulary.
/// Every other refusal on this surface says what was wrong with what the caller sent and what to
/// send instead.
/// </para>
/// <para>
/// The tool's own input schema is what answers it, so nothing here has to guess: each supplied
/// argument's JSON kind is checked against the type the schema declares for it, and the first
/// disagreement is the one reported. Run only once the binder has already refused the call, so a
/// schema this cannot read, or a coercion the binder is willing to make, cannot turn a working call
/// into a refusal.
/// </para>
/// <para>
/// A misspelled name is the other half of the same rule. The binder does not refuse it: it drops
/// it and binds the declared argument at its default, so nothing downstream of the binder can tell
/// it was ever sent. The schema is again what answers it -- see <see cref="Undeclared"/> -- and it
/// is said whether the call then fails or succeeds, since one that succeeds without the argument
/// has answered a different question.
/// </para>
/// <para>
/// Here rather than in each host because there are three MCP boundaries and one is in an assembly
/// the test projects cannot reference. It is pure <c>System.Text.Json</c> over a schema and a
/// dictionary, with no dependency on the MCP packages, so it stays inside what this assembly is for.
/// </para>
/// </summary>
public static class ToolArgumentShape
{
	/// <summary>
	/// A sentence naming the argument whose shape does not match the schema, or null when every
	/// supplied argument matches and the refusal was about something else.
	/// <para>
	/// An argument sent as null is passed over rather than measured. A nullable argument declares null
	/// among its types, and an argument that is not nullable is a different refusal -- something
	/// required and missing, which the binder already says clearly.
	/// </para>
	/// </summary>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	public static string? Mismatch(JsonElement inputSchema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		if (arguments is null) return null;
		if (Properties(inputSchema) is not { } properties) return null;

		foreach (var (name, value) in arguments)
		{
			if (value.ValueKind == JsonValueKind.Null) continue;
			if (!properties.TryGetProperty(name, out var declared)) continue;

			var wanted = Types(declared);
			if (wanted.Count == 0) continue;
			if (wanted.Any(type => Matches(type, value.ValueKind))) continue;

			return $"{name} takes {Article(Wanted(declared, wanted[0]))} {Wanted(declared, wanted[0])}, and "
				+ $"{Article(Sent(value.ValueKind))} {Sent(value.ValueKind)} was sent. "
				+ $"Send it as {Example(declared, wanted[0])}.";
		}

		return null;
	}

	/// <summary>
	/// The arguments a call carried that the tool's schema does not declare, each with the declared
	/// names it most likely meant.
	/// <para>
	/// An argument name is part of a tool's vocabulary, so a name the tool does not know is a caller
	/// error the tool can see and should say, exactly as it says a wrong-shaped value. The binder
	/// drops such an argument rather than refusing it and runs the call with the declared one at its
	/// default, so a refusal then reports as missing a value the caller did send, and a call that
	/// succeeds answers a question the caller did not ask. Named rather than refused: clients attach
	/// extras of their own, and refusing them would break calls that work.
	/// </para>
	/// </summary>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	/// <returns>
	/// Empty where every argument is declared, and where the schema declares no properties this can
	/// read -- a schema this cannot read is no evidence that any name is wrong.
	/// </returns>
	public static IReadOnlyList<UndeclaredArgument> Undeclared(
		JsonElement inputSchema,
		IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		if (arguments is null) return [];
		if (Properties(inputSchema) is not { } properties) return [];

		var declared = Declared(properties);

		return
		[
			.. arguments
				.Select(argument => argument.Key)
				.Where(name => !properties.TryGetProperty(name, out _))
				.Select(name => new UndeclaredArgument(name, Closest(name, declared))),
		];
	}

	/// <summary>
	/// What to add to a refusal when the call carried arguments the tool does not declare: one
	/// sentence per argument, naming it and the declared name it most likely meant, or null when
	/// every argument is declared.
	/// <para>
	/// Added to every refusal rather than only to one about a missing value, because which refusals
	/// those are is known only at the throw sites, and an undeclared name is a mistake worth naming
	/// whatever else went wrong. It is usually the reason: the value the refusal calls missing is the
	/// one sent under the wrong name.
	/// </para>
	/// </summary>
	/// <param name="tool">The tool's name, as the caller called it.</param>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	public static string? NotArguments(
		string tool,
		JsonElement inputSchema,
		IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		var undeclared = Undeclared(inputSchema, arguments);
		if (undeclared.Count == 0) return null;

		var declared = Declared(Properties(inputSchema));

		return string.Join(
			" ",
			undeclared.Select(argument => $"`{argument.Name}` is not an argument of `{tool}`. {Instead(tool, argument, declared)}"));
	}

	/// <summary>
	/// One notice per argument a call carried that the tool does not declare, for a call that ran
	/// without them.
	/// <para>
	/// This is the expensive half of the mistake. A refusal at least stops, but a call that binds
	/// without its misspelled argument runs anyway -- a reference search meant for one project searches
	/// the solution -- and returns a well-formed answer to a different question, with nothing in it
	/// saying an argument was set aside.
	/// </para>
	/// </summary>
	/// <param name="tool">The tool's name, as the caller called it.</param>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	public static IReadOnlyList<string> Ignored(
		string tool,
		JsonElement inputSchema,
		IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		var undeclared = Undeclared(inputSchema, arguments);
		if (undeclared.Count == 0) return [];

		var declared = Declared(Properties(inputSchema));

		return
		[
			.. undeclared.Select(argument =>
				$"Ignored an argument called `{argument.Name}`; `{tool}` has no such argument. {Instead(tool, argument, declared)}"),
		];
	}

	/// <summary>
	/// The message for a call a tool refused: the argument whose shape the binder could not take
	/// where that was the refusal, the refusal's own words otherwise, and after either, every
	/// argument the call carried that the tool does not declare.
	/// <para>
	/// The one composition all three MCP boundaries use, so a refusal reads the same whichever
	/// process wrote it.
	/// </para>
	/// </summary>
	/// <param name="message">What the refusal said.</param>
	/// <param name="binderRefused">
	/// Whether the refusal is the JSON binder's own, which names a CLR type rather than an argument
	/// and is replaced by <see cref="Mismatch"/> where the schema can say which argument it was.
	/// </param>
	/// <param name="tool">The tool's name, as the caller called it.</param>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	public static string Refusal(
		string message,
		bool binderRefused,
		string tool,
		JsonElement inputSchema,
		IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		var reason = binderRefused ? Mismatch(inputSchema, arguments) ?? message : message;
		if (NotArguments(tool, inputSchema, arguments) is not { } undeclared) return reason;

		var trimmed = reason.TrimEnd();
		var endsASentence = trimmed.Length > 0 && ".?!".Contains(trimmed[^1]);

		return endsASentence ? $"{trimmed} {undeclared}" : $"{trimmed}. {undeclared}";
	}

	/// <summary>
	/// What to send instead: the nearest declared names where any is close, and otherwise every name
	/// the tool takes, since a caller that guessed wrong once will guess again without the list.
	/// </summary>
	private static string Instead(string tool, UndeclaredArgument argument, IReadOnlyList<string> declared)
	{
		if (argument.Closest.Count > 0) return $"Did you mean {Quoted(argument.Closest, "or")}?";
		if (declared.Count == 0) return $"`{tool}` takes no arguments.";

		return $"It takes {Quoted(declared, "and")}.";
	}

	/// <summary>Names in backticks, as a list a sentence can carry.</summary>
	private static string Quoted(IReadOnlyList<string> names, string conjunction)
	{
		var quoted = names.Select(name => $"`{name}`").ToList();

		return quoted.Count == 1
			? quoted[0]
			: $"{string.Join(", ", quoted[..^1])} {conjunction} {quoted[^1]}";
	}

	/// <summary>
	/// The schema's <c>properties</c> object, or null where it has none this can read. A tool with no
	/// arguments still declares an empty one, so null means a schema of some other shape.
	/// </summary>
	private static JsonElement? Properties(JsonElement inputSchema)
	{
		if (inputSchema.ValueKind != JsonValueKind.Object) return null;
		if (!inputSchema.TryGetProperty("properties", out var properties)) return null;

		return properties.ValueKind == JsonValueKind.Object ? properties : null;
	}

	/// <summary>The declared argument names, in the schema's order.</summary>
	private static IReadOnlyList<string> Declared(JsonElement? properties) =>
		properties is { } declared ? [.. declared.EnumerateObject().Select(property => property.Name)] : [];

	/// <summary>
	/// The declared names nearest to one that was sent, all of them where several are equally near.
	/// </summary>
	private static IReadOnlyList<string> Closest(string sent, IReadOnlyList<string> declared)
	{
		var near = declared
			.Select(name => (Name: name, Edits: Nearness(sent, name)))
			.Where(candidate => candidate.Edits is not null)
			.ToList();

		if (near.Count == 0) return [];

		var nearest = near.Min(candidate => candidate.Edits);

		return [.. near.Where(candidate => candidate.Edits == nearest).Select(candidate => candidate.Name)];
	}

	/// <summary>
	/// How many edits apart a sent name and a declared one are, or null where they are too far apart
	/// for one to be the other.
	/// <para>
	/// Two ways to be close, because the mistakes are of two kinds. A typo is a few edits in a name
	/// of the same length -- <c>symbl</c>, <c>fliePath</c> -- and is allowed one edit in three. A
	/// guess is a different spelling of the same idea, and usually a part of it: <c>file</c> and
	/// <c>path</c> are each four edits from <c>filePath</c>, far more than any typo, and still
	/// plainly what was meant. Case is ignored by both, since nothing is ever declared twice in two
	/// cases. Ranked by edits either way, so <c>path</c> picks <c>filePath</c> over
	/// <c>projectPath</c>.
	/// </para>
	/// </summary>
	private static int? Nearness(string sent, string declared)
	{
		var one = sent.ToLowerInvariant();
		var other = declared.ToLowerInvariant();
		var edits = Edits(one, other);
		var shorter = Math.Min(one.Length, other.Length);

		var isTypo = edits <= Math.Max(1, shorter / 3);
		var isPart = shorter >= 3 && (one.Contains(other, StringComparison.Ordinal) || other.Contains(one, StringComparison.Ordinal));

		return isTypo || isPart ? edits : null;
	}

	/// <summary>
	/// Edit distance counting a swap of two neighbouring letters as one edit, which is the typo a
	/// hand makes most and the one a plain Levenshtein distance counts twice.
	/// </summary>
	private static int Edits(string one, string other)
	{
		var distance = new int[one.Length + 1][];
		for (var i = 0; i <= one.Length; i++)
		{
			distance[i] = new int[other.Length + 1];
			distance[i][0] = i;
		}

		for (var j = 0; j <= other.Length; j++) distance[0][j] = j;

		for (var i = 1; i <= one.Length; i++)
		{
			for (var j = 1; j <= other.Length; j++)
			{
				var substitution = one[i - 1] == other[j - 1] ? 0 : 1;
				distance[i][j] = Math.Min(
					Math.Min(distance[i - 1][j] + 1, distance[i][j - 1] + 1),
					distance[i - 1][j - 1] + substitution);

				var isSwap = i > 1 && j > 1 && one[i - 1] == other[j - 2] && one[i - 2] == other[j - 1];
				if (isSwap) distance[i][j] = Math.Min(distance[i][j], distance[i - 2][j - 2] + 1);
			}
		}

		return distance[one.Length][other.Length];
	}

	/// <summary>
	/// The types a schema property allows. A nullable argument is declared as a union, so this is a
	/// list rather than one name, and "null" is dropped: it is always allowed and never the thing
	/// the caller got wrong.
	/// </summary>
	private static IReadOnlyList<string> Types(JsonElement declared)
	{
		if (!declared.TryGetProperty("type", out var type)) return [];

		return type.ValueKind switch
		{
			JsonValueKind.String when type.GetString() is { Length: > 0 } one and not "null" => [one],
			JsonValueKind.Array =>
			[
				.. type.EnumerateArray()
					.Where(entry => entry.ValueKind == JsonValueKind.String)
					.Select(entry => entry.GetString())
					.OfType<string>()
					.Where(entry => entry != "null"),
			],
			_ => [],
		};
	}

	/// <summary>Whether a value of this JSON kind satisfies a schema type.</summary>
	private static bool Matches(string type, JsonValueKind kind) => type switch
	{
		"array" => kind == JsonValueKind.Array,
		"object" => kind == JsonValueKind.Object,
		"string" => kind == JsonValueKind.String,
		"integer" or "number" => kind == JsonValueKind.Number,
		"boolean" => kind is JsonValueKind.True or JsonValueKind.False,

		// A type this does not know is not a mismatch. Being wrong about the schema would name an
		// argument that is fine, for a call the binder refused for some other reason entirely.
		_ => true,
	};

	/// <summary>The wanted type as the caller would say it, with an array's element type named.</summary>
	private static string Wanted(JsonElement declared, string type)
	{
		if (type != "array") return type;

		var items = declared.TryGetProperty("items", out var element) ? Types(element).FirstOrDefault() : null;

		return items is null ? "list" : $"list of {items}s";
	}

	/// <summary>What arrived, in the same vocabulary.</summary>
	private static string Sent(JsonValueKind kind) => kind switch
	{
		JsonValueKind.Array => "list",
		JsonValueKind.Object => "object",
		JsonValueKind.String => "string",
		JsonValueKind.Number => "number",
		JsonValueKind.True or JsonValueKind.False => "boolean",
		_ => "null",
	};

	private static string Article(string word) =>
		word.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";

	/// <summary>
	/// What to send instead, spelled as JSON. A list is the case worth showing: the mistake is
	/// sending one element bare, and seeing the brackets is the whole correction.
	/// </summary>
	private static string Example(JsonElement declared, string type) => type switch
	{
		"array" when declared.TryGetProperty("items", out var element) && Types(element).FirstOrDefault() == "string" =>
			"[\"one\", \"two\"]",
		"array" => "a JSON array",
		"string" => "\"text\"",
		"integer" or "number" => "12",
		"boolean" => "true or false",
		_ => $"{Article(type)} {type}",
	};
}
