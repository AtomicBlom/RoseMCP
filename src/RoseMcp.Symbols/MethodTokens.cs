using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RoseMcp.Symbols;

/// <summary>
/// Metadata lookups done from the module file on disk with System.Reflection.Metadata, so nothing
/// here has to touch IMetaDataImport. The debugger only needs tokens, and a token is the same
/// whether it was read here or through COM.
/// <para>
/// Every lookup goes through <see cref="SymbolCache"/> rather than opening the file itself. Naming
/// one method is one read, so a stack walk that names twenty frames and their locals was opening and
/// parsing the same few assemblies dozens of times, on the path a person is waiting on.
/// </para>
/// </summary>
public static class MethodTokens
{
	/// <summary>The method-def token for a type and method name, or null when the module has no such method.</summary>
	public static int? Find(string modulePath, string typeName, string methodName)
	{
		if (Read(modulePath) is not { } metadata) return null;

		foreach (var typeHandle in metadata.TypeDefinitions)
		{
			var type = metadata.GetTypeDefinition(typeHandle);
			if (FullName(metadata, type) != typeName) continue;

			foreach (var methodHandle in type.GetMethods())
			{
				var method = metadata.GetMethodDefinition(methodHandle);
				if (metadata.StringComparer.Equals(method.Name, methodName))
				{
					return MetadataTokens.GetToken(methodHandle);
				}
			}
		}

		return null;
	}

	public static string? TypeName(string modulePath, int typeToken)
	{
		try
		{
			if (Read(modulePath) is not { } metadata) return null;

			var handle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(typeToken);

			return FullName(metadata, metadata.GetTypeDefinition(handle));
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The metadata token of a field on a type, by name, for reading it off a stopped object value
	/// (safe field-access evaluation). Walks the type's own fields; returns null when there is no such
	/// field, so a field-access expression against the wrong type fails cleanly.
	/// </summary>
	public static int? FieldToken(string modulePath, int typeToken, string fieldName)
	{
		try
		{
			if (Read(modulePath) is not { } metadata) return null;

			var type = metadata.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.EntityHandle(typeToken));

			foreach (var fieldHandle in type.GetFields())
			{
				var field = metadata.GetFieldDefinition(fieldHandle);
				if (metadata.StringComparer.Equals(field.Name, fieldName))
				{
					return MetadataTokens.GetToken(fieldHandle);
				}
			}

			return null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The fields a type declares, for listing what a value holds.
	/// <para>
	/// This type's own only. A base class's fields are as much a part of the object, but the base
	/// may be declared in another module, so walking the chain is the caller's -- it holds the live
	/// type and can ask the runtime for each level rather than guessing at a type reference.
	/// </para>
	/// </summary>
	public static IReadOnlyList<FieldMember> Fields(string modulePath, int typeToken)
	{
		try
		{
			if (Read(modulePath) is not { } metadata) return [];

			var type = metadata.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.EntityHandle(typeToken));
			var fields = new List<FieldMember>();

			foreach (var fieldHandle in type.GetFields())
			{
				var field = metadata.GetFieldDefinition(fieldHandle);
				var isStatic = (field.Attributes & FieldAttributes.Static) != 0;

				fields.Add(new FieldMember
				{
					Name = metadata.GetString(field.Name),
					Token = MetadataTokens.GetToken(fieldHandle),
					IsStatic = isStatic,
				});
			}

			return fields;
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary>
	/// A method's parameter names in order and whether it is static, for naming a stopped frame's
	/// arguments. An instance method's argument 0 is <c>this</c>, which these names do not include.
	/// </summary>
	public static (bool IsStatic, IReadOnlyList<string> Names) ParameterNames(string modulePath, int methodToken)
	{
		try
		{
			if (Read(modulePath) is not { } metadata) return (true, []);

			var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
			var method = metadata.GetMethodDefinition(handle);
			var isStatic = (method.Attributes & MethodAttributes.Static) != 0;

			var names = new List<string>();
			foreach (var parameterHandle in method.GetParameters())
			{
				var parameter = metadata.GetParameter(parameterHandle);
				if (parameter.SequenceNumber == 0) continue; // The return parameter, not an argument.
				names.Add(metadata.GetString(parameter.Name));
			}

			return (isStatic, names);
		}
		catch (Exception)
		{
			return (true, []);
		}
	}

	/// <summary>The declaring type's full name plus the method name, for a method-def token.</summary>
	public static string? MethodFullName(string modulePath, int methodToken)
	{
		try
		{
			if (Read(modulePath) is not { } metadata) return null;

			var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
			var method = metadata.GetMethodDefinition(handle);
			var typeName = FullName(metadata, metadata.GetTypeDefinition(method.GetDeclaringType()));

			return $"{typeName}.{metadata.GetString(method.Name)}";
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static MetadataReader? Read(string modulePath) => SymbolCache.Shared.For(modulePath)?.Metadata;

	/// <summary>
	/// A type's name as metadata spells it: namespace-qualified, with a <c>+</c> before each nesting
	/// level. That spelling is the one a location string carries, so it is what a lookup compares
	/// against and what a search hands back.
	/// </summary>
	internal static string FullName(MetadataReader metadata, TypeDefinition type)
	{
		var name = metadata.GetString(type.Name);
		var declaring = type.GetDeclaringType();
		if (!declaring.IsNil)
		{
			return FullName(metadata, metadata.GetTypeDefinition(declaring)) + "+" + name;
		}

		var ns = metadata.GetString(type.Namespace);
		return ns.Length == 0 ? name : ns + "." + name;
	}
}

/// <summary>One field on a type, as metadata describes it.</summary>
public sealed record FieldMember
{
	public required string Name { get; init; }

	/// <summary>The field-def token, which is what reads its value off an object.</summary>
	public required int Token { get; init; }

	/// <summary>
	/// Whether it belongs to the type rather than an instance. Kept rather than filtered, so a
	/// caller listing what an object holds can leave statics out and one asking about a type can not.
	/// </summary>
	public required bool IsStatic { get; init; }
}
