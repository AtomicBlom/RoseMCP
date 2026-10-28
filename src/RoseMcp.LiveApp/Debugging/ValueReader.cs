using System.Globalization;
using System.Runtime.InteropServices;

using ClrDebug;

using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Renders a stopped frame's variable to a type name, a short value string, and whether there is
/// anything inside it worth expanding. Fixed-size primitives and strings get real values. An object
/// is shown by what memory alone can say of it: an enum as its member's name, a nullable, date or
/// duration as its value, a small struct as <c>{TypeName}</c> and its fields, a framework collection
/// as <c>{TypeName}</c> and its count -- and anything else as <c>{TypeName}</c>, because reading an
/// object's own ToString means running the debuggee's code, which nothing here does. Every read is
/// defensive: a value that cannot be read reports so rather than throwing, so one unreadable local
/// does not lose the rest of the frame.
/// </summary>
internal static class ValueReader
{
	/// <summary>
	/// How much of a string comes back when nothing asks for more. A frame can hold twenty locals
	/// and each of them a document, so the default is what keeps reading a frame from being a
	/// transfer of the target's heap.
	/// </summary>
	internal const int DefaultMaxStringLength = 200;

	/// <summary>
	/// The most a caller can ask for on one value. There is a ceiling because the answer goes into a
	/// model's context whole and an unbounded one cannot be recovered from, and it is this high
	/// because the values that hit the default -- a URL, a connection string, a JSON body, a SQL
	/// statement -- are the whole reason somebody set the breakpoint. A value longer than this
	/// reports its length, so the caller knows what is missing rather than guessing.
	/// </summary>
	internal const int MaxRequestedStringLength = 65536;

	private const int MaxDepth = 2;

	/// <summary>
	/// A value as it reads: its type, the text of it, whether it expands, and -- only when the text
	/// is shorter than the value -- how long the value really is.
	/// </summary>
	internal sealed record ReadValue(string? TypeName, string? Value, bool HasChildren, int? FullLength = null);

	/// <summary>
	/// Reads a value. <c>HasChildren</c> says whether expanding it would yield anything, so a tree
	/// can show an expander only where there is something behind it without paying a read per row.
	/// <para>
	/// <paramref name="maxStringLength"/> is how much of a string to render. The whole string is read
	/// off the target either way -- the cap is on what is reported, not on what is fetched -- so
	/// raising it for one value costs nothing but the bytes it returns.
	/// </para>
	/// </summary>
	public static ReadValue Read(CorDebugValue value, int maxStringLength = DefaultMaxStringLength)
		=> Read(value, 0, maxStringLength);

	private static ReadValue Read(CorDebugValue value, int depth, int maxStringLength = DefaultMaxStringLength)
	{
		try
		{
			var elementType = value.Type;

			if (value is CorDebugReferenceValue reference)
			{
				if (reference.IsNull) return new ReadValue(FriendlyType(elementType), "null", false);

				// Past the depth limit there is still something there, so it is expandable even
				// though the value string says nothing about it.
				if (depth >= MaxDepth) return new ReadValue(FriendlyType(elementType), "(...)", true);

				return Read(reference.Dereference(), depth + 1, maxStringLength);
			}

			if (value is CorDebugStringValue stringValue)
			{
				// The whole string comes off the target and only the rendering is capped, so what a
				// caller asking for more gets back is the same read, not a second trip.
				var whole = stringValue.GetString(stringValue.Length);
				var kept = Math.Min(whole?.Length ?? 0, Math.Max(0, maxStringLength));

				return new ReadValue(
					"string",
					Quote(whole, maxStringLength),
					false,
					whole is not null && kept < whole.Length ? whole.Length : null);
			}

			if (value is CorDebugArrayValue arrayValue)
			{
				var count = Count(arrayValue);
				return new ReadValue(FriendlyType(elementType), $"{{{FriendlyType(elementType)}[{count}]}}", count > 0);
			}

			if (value is CorDebugBoxValue) return new ReadValue(FriendlyType(elementType), "(boxed)", true);

			if (value is CorDebugObjectValue objectValue)
			{
				var typeName = ObjectTypeName(objectValue) ?? FriendlyType(elementType);
				if (Describe(objectValue, elementType, typeName, depth, maxStringLength) is { } described) return described;

				// Reported as expandable without asking whether the type declares a field. The check
				// is a metadata read per value on a path a person is waiting on, and being wrong
				// costs an expander that opens onto nothing.
				return new ReadValue(typeName, $"{{{typeName}}}", true);
			}

			if (value is CorDebugGenericValue genericValue && TryReadPrimitive(genericValue, elementType, out var text))
			{
				return new ReadValue(FriendlyType(elementType), text, false);
			}

			return new ReadValue(FriendlyType(elementType), null, false);
		}
		catch (Exception)
		{
			return new ReadValue(null, "(unreadable)", false);
		}
	}

	private static int Count(CorDebugArrayValue value)
	{
		try
		{
			return value.Count;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	/// <summary>
	/// The most fields a struct may have and still be shown on one line. Past it the line is a wall a
	/// reader skims rather than reads, and expanding the value says the same thing laid out.
	/// </summary>
	private const int MaxSummarisedFields = 6;

	/// <summary>How much of a string field a struct's one-line summary carries.</summary>
	private const int SummaryStringLength = 40;

	/// <summary>How many wrappers a collection's count is followed through, such as an ObservableCollection's list.</summary>
	private const int MaxCountHops = 3;

	/// <summary>
	/// The fields holding a collection's count, by the collection's type as metadata spells it, each
	/// with the count of free slots to take off it where the type keeps one. More than one spelling
	/// where the runtimes differ: a UWP app runs a framework older than the one this host does, and its
	/// fields were named before the underscore convention.
	/// </summary>
	private static readonly Dictionary<string, (string Count, string? Free)[]> CountFields = new(StringComparer.Ordinal)
	{
		["System.Collections.Generic.List`1"] = [("_size", null)],
		["System.Collections.Generic.Queue`1"] = [("_size", null)],
		["System.Collections.Generic.Stack`1"] = [("_size", null)],
		["System.Collections.Generic.Dictionary`2"] = [("_count", "_freeCount"), ("count", "freeCount")],
		["System.Collections.Generic.HashSet`1"] = [("_count", "_freeCount"), ("m_count", null)],
		["System.Collections.Generic.SortedSet`1"] = [("count", null)],
		["System.Collections.Generic.LinkedList`1"] = [("count", null)],
		["System.Collections.ArrayList"] = [("_size", null)],
		["System.Collections.Hashtable"] = [("_count", null), ("count", null)],
	};

	/// <summary>
	/// Collections whose count is the count of the collection they hold, and the field holding it.
	/// ObservableCollection derives from Collection, so the walk up its bases finds it here.
	/// </summary>
	private static readonly Dictionary<string, string> WrappedCollections = new(StringComparer.Ordinal)
	{
		["System.Collections.ObjectModel.Collection`1"] = "items",
		["System.Collections.ObjectModel.ReadOnlyCollection`1"] = "list",
	};

	/// <summary>
	/// Says what an object holds where that fits in a line and can be read without running anything:
	/// an enum's name, the value of a nullable, a date or a duration, a small struct's fields, or a
	/// collection's count. Null when it is none of those, and the caller shows the type alone.
	/// <para>
	/// All of it is memory read through the same field lookup a path uses. A <c>ToString</c> or a
	/// <c>Count</c> getter would say it better and would mean running the debuggee's code on a callback
	/// the target is stopped for, which is how a debugger deadlocks the thing it is watching.
	/// </para>
	/// </summary>
	private static ReadValue? Describe(CorDebugObjectValue value, CorElementType elementType, string typeName, int depth, int maxStringLength)
	{
		try
		{
			if (elementType != CorElementType.ValueType) return Counted(value, typeName);

			return Enumerated(value, typeName)
				?? WellKnown(value, typeName, depth, maxStringLength)
				?? Summarised(value, typeName, depth);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// An enum by its member's name, or its members joined for flags, or the number when no member is
	/// the value -- which any integer can be, so it is shown rather than mistaken for unreadable.
	/// </summary>
	private static ReadValue? Enumerated(CorDebugObjectValue value, string typeName)
	{
		var cls = value.Class;
		if (EnumShape.Of(cls.Module.Name, (int)cls.Token) is not { } shape) return null;

		if (CorDebugInspector.ResolveField(value, "value__") is not CorDebugGenericValue underlying) return null;
		if (!TryReadPrimitive(underlying, underlying.Type, out var number)) return null;

		// Widened the way the shape widens its constants: a signed value sign-extends, so -1 compares
		// equal to a member declared as -1 whatever its width.
		var bits = long.TryParse(number, NumberStyles.Integer, CultureInfo.CurrentCulture, out var signed)
			? unchecked((ulong)signed)
			: ulong.Parse(number, NumberStyles.Integer, CultureInfo.CurrentCulture);

		return new ReadValue(typeName, shape.Name(bits) ?? number, false);
	}

	/// <summary>
	/// The framework structs whose fields say nothing on their own: a nullable is null or its value, a
	/// <see cref="DateTime"/> packs its kind into the top bits of its ticks, and a
	/// <see cref="TimeSpan"/> is a count of ticks nobody reads as a duration.
	/// </summary>
	private static ReadValue? WellKnown(CorDebugObjectValue value, string typeName, int depth, int maxStringLength)
	{
		switch (typeName)
		{
			case "System.Nullable`1":
			{
				if (!TryReadField(value, "hasValue", out var hasValue)) return null;
				if (hasValue != "True") return new ReadValue(typeName, "null", false);
				if (CorDebugInspector.ResolveField(value, "value") is not { } inner) return null;

				var read = Read(inner, depth, maxStringLength);
				return read with { TypeName = read.TypeName is { } innerType ? $"{innerType}?" : typeName };
			}

			case "System.DateTime":
			{
				if (!TryReadField(value, "_dateData", out var data)) return null;

				var packed = ulong.Parse(data, NumberStyles.Integer, CultureInfo.CurrentCulture);
				var ticks = (long)(packed & 0x3FFFFFFFFFFFFFFF);
				var kind = (packed >> 62) switch
				{
					0 => DateTimeKind.Unspecified,
					1 => DateTimeKind.Utc,
					_ => DateTimeKind.Local,
				};

				return new ReadValue(typeName, new DateTime(ticks, kind).ToString("o", CultureInfo.InvariantCulture), false);
			}

			case "System.TimeSpan":
			{
				if (!TryReadField(value, "_ticks", out var ticks)) return null;

				var span = TimeSpan.FromTicks(long.Parse(ticks, NumberStyles.Integer, CultureInfo.CurrentCulture));
				return new ReadValue(typeName, span.ToString("c", CultureInfo.InvariantCulture), false);
			}

			default:
				return null;
		}
	}

	/// <summary>
	/// A small struct's fields on one line, each under the name the source gives it, so a size or a
	/// point reads as its values rather than as its type.
	/// <para>
	/// Only for a struct read in its own right. One nested inside another shows its type, because two
	/// levels of comma-separated fields on one line cannot be told apart; expanding it lays them out.
	/// </para>
	/// </summary>
	private static ReadValue? Summarised(CorDebugObjectValue value, string typeName, int depth)
	{
		if (depth > 0) return null;

		var cls = value.Class;
		var fields = MethodTokens.Fields(cls.Module.Name, cls.Token).Where(field => !field.IsStatic).ToList();
		if (fields.Count is 0 or > MaxSummarisedFields) return null;

		var parts = new List<string>(fields.Count);
		foreach (var field in fields)
		{
			var read = Read(value.GetFieldValue(cls.Raw, field.Token), depth + 1, SummaryStringLength);
			if (read.Value is null) return null;

			parts.Add($"{MethodTokens.PropertyBackedBy(field.Name) ?? field.Name} = {read.Value}");
		}

		return new ReadValue(typeName, $"{{{typeName}}} {string.Join(", ", parts)}", true);
	}

	/// <summary>
	/// A collection by its count, when its type or one of its bases is a framework collection whose
	/// count field is known. Anything else -- a user's own collection, an immutable one, a projected
	/// WinRT vector -- keeps its type alone, because the only general way to count it is to call it.
	/// </summary>
	private static ReadValue? Counted(CorDebugObjectValue value, string typeName) =>
		CountOf(value, MaxCountHops) is { } count ? new ReadValue(typeName, $"{{{typeName}}} Count = {count}", true) : null;

	private static int? CountOf(CorDebugValue value, int hops)
	{
		var unwrapped = value is CorDebugReferenceValue { IsNull: false } reference ? reference.Dereference() : value;
		if (unwrapped is CorDebugArrayValue array) return Count(array);
		if (unwrapped is not CorDebugObjectValue objectValue) return null;

		for (var type = objectValue.ExactType; type is not null; type = BaseOrNull(type))
		{
			var cls = type.Class;
			if (MethodTokens.TypeName(cls.Module.Name, cls.Token) is not { } name) continue;

			if (CountFields.TryGetValue(name, out var spellings))
			{
				foreach (var (countField, freeField) in spellings)
				{
					if (!TryReadField(objectValue, countField, out var count)) continue;

					var free = freeField is not null && TryReadField(objectValue, freeField, out var freeText) ? int.Parse(freeText, CultureInfo.CurrentCulture) : 0;
					return int.Parse(count, CultureInfo.CurrentCulture) - free;
				}

				return null;
			}

			var wraps = WrappedCollections.TryGetValue(name, out var inner) && hops > 0;
			if (wraps) return CorDebugInspector.ResolveField(objectValue, inner!) is { } held ? CountOf(held, hops - 1) : null;
		}

		return null;
	}

	private static CorDebugType? BaseOrNull(CorDebugType type)
	{
		try
		{
			return type.Base;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A primitive field's text, read by name through the same lookup a path uses.</summary>
	private static bool TryReadField(CorDebugValue value, string field, out string text)
	{
		text = string.Empty;

		return CorDebugInspector.ResolveField(value, field) is CorDebugGenericValue generic && TryReadPrimitive(generic, generic.Type, out text);
	}

	private static bool TryReadPrimitive(CorDebugGenericValue value, CorElementType elementType, out string text)
	{
		text = string.Empty;
		if (!IsFixedPrimitive(elementType)) return false;

		// A fixed primitive is at most 8 bytes; GetValue writes that many into the buffer.
		var buffer = Marshal.AllocHGlobal(8);
		try
		{
			value.GetValue(buffer);
			object boxed = elementType switch
			{
				CorElementType.Boolean => Marshal.ReadByte(buffer) != 0,
				CorElementType.Char => (char)(ushort)Marshal.ReadInt16(buffer),
				CorElementType.I1 => (sbyte)Marshal.ReadByte(buffer),
				CorElementType.U1 => Marshal.ReadByte(buffer),
				CorElementType.I2 => Marshal.ReadInt16(buffer),
				CorElementType.U2 => (ushort)Marshal.ReadInt16(buffer),
				CorElementType.I4 => Marshal.ReadInt32(buffer),
				CorElementType.U4 => (uint)Marshal.ReadInt32(buffer),
				CorElementType.I8 => Marshal.ReadInt64(buffer),
				CorElementType.U8 => (ulong)Marshal.ReadInt64(buffer),
				CorElementType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer)),
				CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)),
				_ => "?",
			};

			text = boxed.ToString() ?? "?";
			return true;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static bool IsFixedPrimitive(CorElementType elementType) => elementType is
		CorElementType.Boolean or CorElementType.Char
		or CorElementType.I1 or CorElementType.U1
		or CorElementType.I2 or CorElementType.U2
		or CorElementType.I4 or CorElementType.U4
		or CorElementType.I8 or CorElementType.U8
		or CorElementType.R4 or CorElementType.R8;

	private static string? ObjectTypeName(CorDebugObjectValue objectValue)
	{
		try
		{
			var cls = objectValue.Class;
			return MethodTokens.TypeName(cls.Module.Name, cls.Token);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// A string as it is reported: quoted, and cut to <paramref name="maxStringLength"/> with an
	/// ellipsis if it is longer. The ellipsis alone cannot say a value was cut -- a string can end
	/// in one -- so the caller is told by <see cref="ReadValue.FullLength"/> rather than by this.
	/// </summary>
	private static string Quote(string? value, int maxStringLength)
	{
		value ??= string.Empty;

		var keep = Math.Max(0, maxStringLength);
		if (value.Length > keep) value = value[..keep] + "…";

		return $"\"{value}\"";
	}

	private static string FriendlyType(CorElementType elementType) => elementType switch
	{
		CorElementType.Boolean => "bool",
		CorElementType.Char => "char",
		CorElementType.I1 => "sbyte",
		CorElementType.U1 => "byte",
		CorElementType.I2 => "short",
		CorElementType.U2 => "ushort",
		CorElementType.I4 => "int",
		CorElementType.U4 => "uint",
		CorElementType.I8 => "long",
		CorElementType.U8 => "ulong",
		CorElementType.R4 => "float",
		CorElementType.R8 => "double",
		CorElementType.String => "string",
		CorElementType.Object => "object",
		_ => elementType.ToString(),
	};
}
