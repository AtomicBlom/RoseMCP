using System.Globalization;
using System.Text;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The line protocol the XAML diagnostics provider speaks, in both directions: the commands an
/// apply sends, and the trees, property lists and results that come back.
/// <para>
/// Apart from the session that carries it, because the two halves have to agree exactly and nothing
/// else about a request does. A command and the lookup of its result are keyed on the same string,
/// so two spellings of an edit's name would apply the edit and then report it as never reported --
/// a failure that needs both ends of the protocol in view at once to see, and neither end of the
/// pipe.
/// </para>
/// <para>
/// Every field arrives escaped, because a value can hold a tab or a newline and the protocol is
/// delimited by both.
/// </para>
/// </summary>
internal static class XamlProviderWire
{
	/// <summary>
	/// The provider's name for an edit kind. One place, because the command and the lookup of its
	/// result have to agree exactly -- they are keyed on this string, so two spellings of it would
	/// apply the edit and then report it as "not reported".
	/// </summary>
	internal static string Op(XamlEditKind kind) => kind switch
	{
		XamlEditKind.SetProperty => "SetProperty",
		XamlEditKind.ClearProperty => "ClearProperty",
		XamlEditKind.RemoveChild => "RemoveChild",
		XamlEditKind.AddChild => "AddChild",
		_ => kind.ToString(),
	};

	/// <summary>
	/// One command line: op, target, property, value type, value, arg, index. The last two are only
	/// used by a structural command, and the shape is fixed so the provider can read positionally
	/// without every command having to carry every field.
	/// </summary>
	internal static string Line(string op, string target, string property, string valueType, string value, string arg, int index) =>
		string.Join('\t', op, target, property, valueType, value, arg, index.ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// How a command's result is found again. The arg is part of it: without it, one slot given two
	/// children produces two rows keyed identically, and the second child's outcome silently replaces
	/// the first's.
	/// </summary>
	internal static string Key(string op, string target, string property, string arg) =>
		string.Join('\t', op, target, property, arg);

	/// <summary>The command for one build step, with the key its result will come back under.</summary>
	internal static (string Line, string Key) Command(XamlStep step) => step.Kind switch
	{
		XamlStepKind.Create => (
			Line("CreateInstance", step.Target, step.TypeName ?? string.Empty, string.Empty, string.Empty, string.Empty, 0),
			Key("CreateInstance", step.Target, step.TypeName ?? string.Empty, string.Empty)),

		XamlStepKind.SetProperty => (
			Line("SetProperty", step.Target, step.Property ?? string.Empty, step.ValueType ?? string.Empty, step.Value ?? string.Empty, string.Empty, 0),
			Key("SetProperty", step.Target, step.Property ?? string.Empty, string.Empty)),

		_ => (
			Line("AddChild", step.Target, string.Empty, string.Empty, string.Empty, step.Child ?? string.Empty, step.Index),
			Key("AddChild", step.Target, string.Empty, step.Child ?? string.Empty)),
	};

	/// <summary>
	/// What to report for one edit, given the commands it turned into.
	/// <para>
	/// An edit built from several commands is only applied if every one of them was. Reporting the last
	/// outcome, or the first, would let an addition whose element was created and then failed to attach
	/// come back as a success -- and the caller would go looking for an element that exists and is in
	/// nobody's tree.
	/// </para>
	/// <para>
	/// A failure inside one of those commands names the command, because the row it lands in carries the
	/// edit's own target and property. An inner SetProperty answering "property not found" reads, in a
	/// SetResource row, as the resource key having been looked up as a property on the element that owns
	/// the dictionary -- a confident wrong account of what went wrong, and one that sends the reader to
	/// the wrong half of the system.
	/// </para>
	/// </summary>
	internal static string Outcome(List<string> keys, Dictionary<string, string> statuses)
	{
		if (keys.Count == 0) return "unsupported: this edit is not applied live yet";

		foreach (var key in keys)
		{
			var status = statuses.GetValueOrDefault(key, "not reported");
			if (status == "applied") continue;

			return keys.Count == 1 ? status : $"{status}, building it: {Describe(key)}";
		}

		return "applied";
	}

	/// <summary>
	/// The command a result key stands for, in the words it was sent in. The key is the command's own
	/// fields, so this needs nothing the plan did not already carry: op, what it was against, and the
	/// property or the child it named.
	/// </summary>
	private static string Describe(string key)
	{
		var fields = key.Split('\t');
		if (fields.Length < 4) return key;

		var subject = fields[2].Length > 0 ? fields[2] : fields[3];
		return subject.Length > 0 ? $"{fields[0]} {subject} on {fields[1]}" : $"{fields[0]} on {fields[1]}";
	}

	internal static Dictionary<string, string> ParseApplyResults(IEnumerable<string> lines)
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 4) continue;

			// Keyed exactly the way the command was sent -- op, target, property, arg -- so each result can
			// be found again. The arg comes after the status and may be missing from an older provider's row.
			var arg = fields.Length > 4 ? Unescape(fields[4]) : string.Empty;
			statuses[Key(fields[0], Unescape(fields[1]), Unescape(fields[2]), arg)] = fields[3];
		}

		return statuses;
	}

	internal static List<LiveXamlNode> ParseTree(IEnumerable<string> lines)
	{
		var nodes = new List<LiveXamlNode>();
		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 5) continue;
			if (!ulong.TryParse(fields[0], out var handle) || !ulong.TryParse(fields[1], out var parent) || !int.TryParse(fields[2], out var childIndex))
			{
				continue;
			}

			var name = Unescape(fields[4]);
			var declaredIn = fields.Length > 5 ? Unescape(fields[5]) : string.Empty;
			var declaredAt = fields.Length > 6 && int.TryParse(fields[6], out var parsedLine) ? parsedLine : 0;

			// A provider older than this host writes no ninth column. Read as "no address" rather than as
			// a bad row: the provider is staged from the install beside us, so the two ship together, but a
			// stale copy left behind in a sandbox would otherwise take the whole tree down with it.
			var address = fields.Length > 8 ? Unescape(fields[8]) : string.Empty;

			nodes.Add(new LiveXamlNode
			{
				Handle = handle,
				Parent = parent,
				ChildIndex = childIndex,
				TypeName = Unescape(fields[3]),
				Name = string.IsNullOrEmpty(name) ? null : name,
				File = string.IsNullOrEmpty(declaredIn) ? null : declaredIn,
				Line = declaredAt > 0 ? declaredAt : null,
				Address = string.IsNullOrEmpty(address) ? null : address,
			});
		}

		return nodes;
	}

	internal static LiveXamlProperties ParseProperties(IEnumerable<string> lines, ulong handle)
	{
		string? typeName = null;
		string? elementFile = null;
		int? elementLine = null;
		int? elementColumn = null;
		var properties = new List<LiveXamlProperty>();

		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields[0] == "E" && fields.Length >= 5)
			{
				typeName = EmptyToNull(Unescape(fields[1]));
				elementFile = EmptyToNull(Unescape(fields[2]));
				elementLine = ParsePositive(fields[3]);
				elementColumn = ParsePositive(fields[4]);
			}
			else if (fields[0] == "P" && fields.Length >= 10)
			{
				var isNull = fields[9] == "1";

				// Length-checked rather than assumed: an older provider staged in a recycled sandbox
				// folder writes ten columns, and the row is still worth reading without the eleventh.
				var unrenderable = fields.Length > 10 && fields[10] == "1";
				properties.Add(new LiveXamlProperty
				{
					Name = Unescape(fields[1]),
					Value = isNull ? null : Unescape(fields[2]),
					ValueUnavailable = unrenderable,
					ValueType = EmptyToNull(Unescape(fields[3])),
					DeclaringType = EmptyToNull(Unescape(fields[4])),
					Provenance = fields[5],
					SourceFile = EmptyToNull(Unescape(fields[6])),
					SourceLine = ParsePositive(fields[7]),
					SourceColumn = ParsePositive(fields[8]),
				});
			}
		}

		return new LiveXamlProperties
		{
			Handle = handle,
			TypeName = typeName,
			SourceFile = elementFile,
			SourceLine = elementLine,
			SourceColumn = elementColumn,
			Properties = properties,
		};
	}

	internal static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

	private static int? ParsePositive(string field) => int.TryParse(field, out var value) && value > 0 ? value : null;

	internal static string Unescape(string field)
	{
		if (field.IndexOf('\\') < 0) return field;

		var builder = new StringBuilder(field.Length);
		for (var i = 0; i < field.Length; i++)
		{
			if (field[i] == '\\' && i + 1 < field.Length)
			{
				var next = field[++i];
				builder.Append(next switch
				{
					't' => '\t',
					'r' => '\r',
					'n' => '\n',
					'\\' => '\\',
					_ => next,
				});
			}
			else
			{
				builder.Append(field[i]);
			}
		}

		return builder.ToString();
	}
}
