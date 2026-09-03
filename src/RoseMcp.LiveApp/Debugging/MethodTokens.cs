using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Metadata lookups done from the module file on disk with System.Reflection.Metadata, so the
/// spike never has to touch IMetaDataImport. The debugger only needs tokens, and a token is the
/// same whether it was read here or through COM.
/// </summary>
internal static class MethodTokens
{
	public static int? Find(string modulePath, string typeName, string methodName)
	{
		using var stream = File.OpenRead(modulePath);
		using var pe = new PEReader(stream);
		var metadata = pe.GetMetadataReader();

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
			using var stream = File.OpenRead(modulePath);
			using var pe = new PEReader(stream);
			var metadata = pe.GetMetadataReader();
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
			using var stream = File.OpenRead(modulePath);
			using var pe = new PEReader(stream);
			var metadata = pe.GetMetadataReader();
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
	/// A method's parameter names in order and whether it is static, for naming a stopped frame's
	/// arguments. An instance method's argument 0 is <c>this</c>, which these names do not include.
	/// </summary>
	public static (bool IsStatic, IReadOnlyList<string> Names) ParameterNames(string modulePath, int methodToken)
	{
		try
		{
			using var stream = File.OpenRead(modulePath);
			using var pe = new PEReader(stream);
			var metadata = pe.GetMetadataReader();
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
			using var stream = File.OpenRead(modulePath);
			using var pe = new PEReader(stream);
			var metadata = pe.GetMetadataReader();
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

	private static string FullName(MetadataReader metadata, TypeDefinition type)
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
