using System.Runtime.InteropServices;

using ClrDebug;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Renders a stopped frame's variable to a type name and a short value string, for the locals and
/// arguments captured at a breakpoint. Fixed-size primitives and strings get real values; an object
/// is shown by its type only, rendered as <c>{TypeName}</c>, because reading an object's own ToString
/// needs func-eval (a later slice), and a struct or enum shows its type without a decoded value. Every
/// read is defensive: a value that cannot be read reports so rather than throwing, so one unreadable
/// local does not lose the rest of the frame.
/// </summary>
internal static class ValueReader
{
	private const int MaxStringLength = 200;
	private const int MaxDepth = 2;

	public static (string? TypeName, string? Value) Read(CorDebugValue value) => Read(value, 0);

	private static (string? TypeName, string? Value) Read(CorDebugValue value, int depth)
	{
		try
		{
			var elementType = value.Type;

			if (value is CorDebugReferenceValue reference)
			{
				if (reference.IsNull) return (FriendlyType(elementType), "null");
				if (depth >= MaxDepth) return (FriendlyType(elementType), "(...)");
				return Read(reference.Dereference(), depth + 1);
			}

			if (value is CorDebugStringValue stringValue)
			{
				return ("string", Quote(stringValue.GetString(stringValue.Length)));
			}

			if (value is CorDebugObjectValue objectValue)
			{
				var typeName = ObjectTypeName(objectValue) ?? FriendlyType(elementType);
				return (typeName, $"{{{typeName}}}");
			}

			if (value is CorDebugGenericValue genericValue && TryReadPrimitive(genericValue, elementType, out var text))
			{
				return (FriendlyType(elementType), text);
			}

			return (FriendlyType(elementType), null);
		}
		catch (Exception)
		{
			return (null, "(unreadable)");
		}
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

	private static string Quote(string? value)
	{
		value ??= string.Empty;
		if (value.Length > MaxStringLength) value = value[..MaxStringLength] + "…";
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
