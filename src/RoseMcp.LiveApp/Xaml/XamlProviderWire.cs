using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Tap.Protocol;
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
/// Every row is made with <see cref="TapWire.Row"/> and taken apart with <see cref="TapWire.Fields"/>,
/// in both directions, because a value can hold a tab or a newline and the protocol is delimited by
/// both. No field here is joined or split any other way.
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
		TapWire.Row(op, target, property, valueType, value, arg, index.ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// How a command's result is found again. The arg is part of it: without it, one slot given two
	/// children produces two rows keyed identically, and the second child's outcome silently replaces
	/// the first's. Made as a row, so a field holding a tab cannot make two different commands' keys
	/// the same string.
	/// </summary>
	internal static string Key(string op, string target, string property, string arg) =>
		TapWire.Row(op, target, property, arg);

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
	internal static string Outcome(List<string> keys, Dictionary<string, XamlApplyRow> results)
	{
		if (keys.Count == 0) return "unsupported: this edit is not applied live yet";

		foreach (var key in keys)
		{
			var status = results.TryGetValue(key, out var row) ? row.Status : "not reported";
			if (status == "applied") continue;

			return keys.Count == 1 ? status : $"{status}, building it: {Describe(key)}";
		}

		return "applied";
	}

	/// <summary>
	/// The element an edit landed on: the handle the provider reported for the first of its commands aimed at the
	/// edit's own target. An addition is built in slots first and attached last, so its slot commands name elements
	/// that were not in the app; the attach is aimed at the parent the edit named, and that is the element a
	/// person finds it under.
	/// </summary>
	internal static ulong? HandleOf(List<string> keys, Dictionary<string, XamlApplyRow> results, string target)
	{
		foreach (var key in keys)
		{
			if (results.TryGetValue(key, out var row) && row.Target == target && row.Handle is { } handle) return handle;
		}

		return null;
	}

	/// <summary>
	/// What the property an edit changed said before it, for an edit that changes one property with one command.
	/// Null for anything else: an addition's slot commands set properties on an element that did not exist a
	/// moment before, so there is no before to speak of.
	/// </summary>
	internal static string? OldValueOf(XamlEditKind kind, List<string> keys, Dictionary<string, XamlApplyRow> results)
	{
		var changesOneProperty = kind is XamlEditKind.SetProperty or XamlEditKind.ClearProperty;
		if (!changesOneProperty || keys.Count != 1) return null;

		return results.TryGetValue(keys[0], out var row) && row.Status == "applied" ? row.Old : null;
	}

	/// <summary>
	/// The command a result key stands for, in the words it was sent in. The key is the command's own
	/// fields, so this needs nothing the plan did not already carry: op, what it was against, and the
	/// property or the child it named.
	/// </summary>
	private static string Describe(string key)
	{
		var fields = TapWire.Fields(key);
		if (fields.Length < 4) return key;

		var subject = fields[2].Length > 0 ? fields[2] : fields[3];
		return subject.Length > 0 ? $"{fields[0]} {subject} on {fields[1]}" : $"{fields[0]} on {fields[1]}";
	}

	internal static Dictionary<string, XamlApplyRow> ParseApplyResults(IEnumerable<string> lines)
	{
		var results = new Dictionary<string, XamlApplyRow>(StringComparer.Ordinal);
		foreach (var line in lines)
		{
			if (XamlWire.ReadApplyRow(line) is not { } row) continue;

			// Keyed exactly the way the command was sent -- op, target, property, arg -- so each result can
			// be found again. The status sits between the property and the arg.
			results[Key(row.Op, row.Target, row.Property, row.Arg)] = row;
		}

		return results;
	}

	internal static List<LiveXamlNode> ParseTree(IEnumerable<string> lines)
	{
		var nodes = new List<LiveXamlNode>();
		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = TapWire.Fields(line);
			if (fields.Length < 9) continue;
			if (!ulong.TryParse(fields[0], out var handle) || !ulong.TryParse(fields[1], out var parent) || !int.TryParse(fields[2], out var childIndex))
			{
				continue;
			}

			var name = fields[4];
			var declaredIn = fields[5];
			var declaredAt = int.TryParse(fields[6], out var parsedLine) ? parsedLine : 0;
			var address = fields[8];

			nodes.Add(new LiveXamlNode
			{
				Handle = handle,
				Parent = parent,
				ChildIndex = childIndex,
				TypeName = fields[3],
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

			var fields = TapWire.Fields(line);
			if (fields[0] == "E" && fields.Length >= 5)
			{
				typeName = EmptyToNull(fields[1]);
				elementFile = EmptyToNull(fields[2]);
				elementLine = ParsePositive(fields[3]);
				elementColumn = ParsePositive(fields[4]);
			}
			else if (fields[0] == "P" && fields.Length >= 11)
			{
				var isNull = fields[9] == "1";
				properties.Add(new LiveXamlProperty
				{
					Name = fields[1],
					Value = isNull ? null : fields[2],
					ValueUnavailable = fields[10] == "1",
					ValueType = EmptyToNull(fields[3]),
					DeclaringType = EmptyToNull(fields[4]),
					Provenance = fields[5],
					SourceFile = EmptyToNull(fields[6]),
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
}
