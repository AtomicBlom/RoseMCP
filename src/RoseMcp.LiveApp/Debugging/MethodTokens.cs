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
