using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using RoseMcp.Contracts;

namespace RoseMcp.Symbols;

/// <summary>
/// The tap an assembly declares, read from its metadata without loading it.
/// <para>
/// Read rather than loaded because a tap is third-party code that only ever runs inside the app it
/// serves: loading it here to ask what it is would run its module initialiser in RoseMCP's own process.
/// The attribute is found by its full name rather than by type identity, because every tap compiles its
/// own copy of it from the contract and no two copies are the same type.
/// </para>
/// </summary>
public static class TapManifest
{
	/// <summary>The namespace of the attribute that marks a tap, as the contract declares it.</summary>
	public const string AttributeNamespace = "RoseMcp.Tap.Protocol";

	/// <summary>The name of the attribute that marks a tap, as the contract declares it.</summary>
	public const string AttributeName = "RoseMcpTapAttribute";

	/// <summary>
	/// The tap <paramref name="path"/> declares, or null when it declares none: an assembly without the
	/// attribute, a native library, or a file that is not an image at all -- all ordinary in a folder a
	/// tap's build output was copied into, beside the libraries it was built against.
	/// </summary>
	public static DroppedInTap? Read(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			using var image = new PEReader(stream);
			if (!image.HasMetadata) return null;

			var reader = image.GetMetadataReader();
			if (!reader.IsAssembly) return null;

			foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
			{
				var attribute = reader.GetCustomAttribute(handle);
				if (!IsTapAttribute(reader, attribute)) continue;

				var value = attribute.DecodeValue(new TypeNames());
				return new DroppedInTap
				{
					Name = Named<string>(value, "Name") ?? string.Empty,
					Module = Named<string>(value, "Module") ?? string.Empty,
					Endpoint = Named<string>(value, "Endpoint") ?? string.Empty,
					EntryType = Named<string>(value, "EntryType") ?? string.Empty,
					Protocol = Named<int>(value, "Protocol"),
					AssemblyPath = Path.GetFullPath(path),
				};
			}

			return null;
		}
		catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>Whether a custom attribute's constructor belongs to the tap attribute, whether the tap defines it or references it.</summary>
	private static bool IsTapAttribute(MetadataReader reader, CustomAttribute attribute)
	{
		StringHandle name;
		StringHandle space;

		switch (attribute.Constructor.Kind)
		{
			case HandleKind.MethodDefinition:
			{
				var type = reader.GetTypeDefinition(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
				(name, space) = (type.Name, type.Namespace);
				break;
			}

			case HandleKind.MemberReference:
			{
				var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
				if (parent.Kind != HandleKind.TypeReference) return false;

				var type = reader.GetTypeReference((TypeReferenceHandle)parent);
				(name, space) = (type.Name, type.Namespace);
				break;
			}

			default:
				return false;
		}

		return reader.StringComparer.Equals(name, AttributeName) && reader.StringComparer.Equals(space, AttributeNamespace);
	}

	/// <summary>A named argument's value, or the default where the attribute does not set it.</summary>
	private static T? Named<T>(CustomAttributeValue<string> value, string name)
	{
		foreach (var argument in value.NamedArguments)
		{
			if (argument.Name == name && argument.Value is T typed) return typed;
		}

		return default;
	}

	/// <summary>
	/// Names for the types in an attribute's arguments, which is all decoding needs here: the tap attribute's
	/// arguments are strings and an int, and nothing is resolved from another assembly.
	/// </summary>
	private sealed class TypeNames : ICustomAttributeTypeProvider<string>
	{
		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

		public string GetSystemType() => "System.Type";

		public string GetSZArrayType(string elementType) => elementType + "[]";

		public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
			reader.GetString(reader.GetTypeDefinition(handle).Name);

		public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
			reader.GetString(reader.GetTypeReference(handle).Name);

		public string GetTypeFromSerializedName(string name) => name;

		public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

		public bool IsSystemType(string type) => type == "System.Type";
	}
}
