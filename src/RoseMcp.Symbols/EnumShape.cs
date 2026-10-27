using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RoseMcp.Symbols;

/// <summary>One named value of an enum: its name, and its constant widened to 64 bits.</summary>
/// <param name="Name">The member's name.</param>
/// <param name="Bits">
/// The constant, sign-extended from a signed underlying type and zero-extended from an unsigned one,
/// which is how a value read off a target has to be widened to compare equal to it.
/// </param>
public readonly record struct EnumMember(string Name, ulong Bits);

/// <summary>
/// An enum's members and whether it is a set of flags, read from its module's metadata, so a value
/// read off a stopped target can be shown by name.
/// <para>
/// A debugger reading memory sees an enum as a struct holding one integer, <c>value__</c>, and an
/// integer is all it would otherwise have to show. The names are constants in metadata, so naming
/// one is a lookup rather than a call to the debuggee's <c>ToString</c>, which nothing here runs.
/// </para>
/// </summary>
public sealed class EnumShape
{
	private EnumShape(IReadOnlyList<EnumMember> members, bool isFlags)
	{
		Members = members;
		IsFlags = isFlags;
	}

	/// <summary>The named values, in declaration order.</summary>
	public IReadOnlyList<EnumMember> Members { get; }

	/// <summary>Whether the type carries <c>[Flags]</c>, so a value may be several members together.</summary>
	public bool IsFlags { get; }

	/// <summary>
	/// The enum declared by this type token, or null when the type is not an enum or the module
	/// cannot be read.
	/// </summary>
	public static EnumShape? Of(string modulePath, int typeToken)
	{
		try
		{
			if (SymbolCache.Shared.For(modulePath)?.Metadata is not { } metadata) return null;

			var type = metadata.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.EntityHandle(typeToken));
			if (!IsNamed(metadata, type.BaseType, "System", "Enum")) return null;

			var members = new List<EnumMember>();
			foreach (var fieldHandle in type.GetFields())
			{
				var field = metadata.GetFieldDefinition(fieldHandle);
				var isMember = (field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal)) == (FieldAttributes.Static | FieldAttributes.Literal);
				if (!isMember) continue;

				if (Widened(metadata, field.GetDefaultValue()) is { } bits) members.Add(new EnumMember(metadata.GetString(field.Name), bits));
			}

			return new EnumShape(members, IsFlagsType(metadata, type));
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The value as C# would print it: a member's name, or for flags the members it is made of joined
	/// with <c>|</c>, smallest first. Null when no member or combination of members is the value,
	/// which is a real state for an enum -- any integer fits -- and the caller shows the number.
	/// </summary>
	public string? Name(ulong bits)
	{
		foreach (var member in Members)
		{
			if (member.Bits == bits) return member.Name;
		}

		if (!IsFlags || bits == 0) return null;

		var remaining = bits;
		var parts = new List<EnumMember>();

		// Largest first, so a member that names several bits at once is preferred over the single
		// bits it is made of, the way Enum.ToString prefers it.
		foreach (var member in Members.Where(member => member.Bits != 0).OrderByDescending(member => member.Bits))
		{
			var contained = (remaining & member.Bits) == member.Bits;
			if (!contained) continue;

			parts.Add(member);
			remaining &= ~member.Bits;
		}

		return remaining == 0 ? string.Join(" | ", parts.OrderBy(member => member.Bits).Select(member => member.Name)) : null;
	}

	private static ulong? Widened(MetadataReader metadata, ConstantHandle handle)
	{
		if (handle.IsNil) return null;

		var constant = metadata.GetConstant(handle);
		var blob = metadata.GetBlobReader(constant.Value);

		return constant.TypeCode switch
		{
			ConstantTypeCode.SByte => unchecked((ulong)blob.ReadSByte()),
			ConstantTypeCode.Byte => blob.ReadByte(),
			ConstantTypeCode.Int16 => unchecked((ulong)blob.ReadInt16()),
			ConstantTypeCode.UInt16 => blob.ReadUInt16(),
			ConstantTypeCode.Int32 => unchecked((ulong)blob.ReadInt32()),
			ConstantTypeCode.UInt32 => blob.ReadUInt32(),
			ConstantTypeCode.Int64 => unchecked((ulong)blob.ReadInt64()),
			ConstantTypeCode.UInt64 => blob.ReadUInt64(),
			ConstantTypeCode.Char => blob.ReadChar(),
			ConstantTypeCode.Boolean => blob.ReadBoolean() ? 1UL : 0UL,
			_ => null,
		};
	}

	private static bool IsFlagsType(MetadataReader metadata, TypeDefinition type)
	{
		foreach (var attributeHandle in type.GetCustomAttributes())
		{
			var constructor = metadata.GetCustomAttribute(attributeHandle).Constructor;
			var declaring = constructor.Kind switch
			{
				HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
				HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
				_ => default,
			};

			if (IsNamed(metadata, declaring, "System", "FlagsAttribute")) return true;
		}

		return false;
	}

	/// <summary>
	/// Whether a handle names this type, whether it is referenced from another module or, inside the
	/// core library, declared alongside the enum.
	/// </summary>
	private static bool IsNamed(MetadataReader metadata, EntityHandle handle, string ns, string name)
	{
		if (handle.IsNil) return false;

		return handle.Kind switch
		{
			HandleKind.TypeReference => metadata.GetTypeReference((TypeReferenceHandle)handle) is var reference
				&& metadata.StringComparer.Equals(reference.Namespace, ns)
				&& metadata.StringComparer.Equals(reference.Name, name),
			HandleKind.TypeDefinition => metadata.GetTypeDefinition((TypeDefinitionHandle)handle) is var definition
				&& metadata.StringComparer.Equals(definition.Namespace, ns)
				&& metadata.StringComparer.Equals(definition.Name, name),
			_ => false,
		};
	}
}
