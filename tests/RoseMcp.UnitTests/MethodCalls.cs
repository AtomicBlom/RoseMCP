using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which methods each method of an assembly calls, read out of its IL, for the tests that pin a rule
/// to one place in the code.
/// <para>
/// Read from the file rather than through reflection, because a method body reached by reflection
/// loads the types of its locals, and the worker's include MSBuild's, which a unit test does not
/// load.
/// </para>
/// <para>
/// A lambda, a local function and the body of an async method are compiled into methods of their
/// own, on types the compiler nests inside the one that wrote them, so a call is credited to the
/// outermost type a person declared. That is the unit the rules are about: which type may ask the
/// question, not which method.
/// </para>
/// </summary>
internal static class MethodCalls
{
	private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(code => code.Value);

	/// <summary>One call: who made it, and what it called, by full name.</summary>
	/// <param name="Owner">The declared type the call was written in, as Namespace.Type.</param>
	/// <param name="Caller">The compiled method holding the call, which is unique within its owner.</param>
	/// <param name="Callee">The method called, as Namespace.Type.Method.</param>
	/// <param name="At">Which instruction of the caller made it, counting from the first.</param>
	public readonly record struct Call(string Owner, string Caller, string Callee, int At);

	/// <summary>Every call in the assembly <paramref name="assembly"/> was loaded from.</summary>
	public static IReadOnlyList<Call> In(Assembly assembly)
	{
		using var stream = File.OpenRead(assembly.Location);
		using var pe = new PEReader(stream);

		var reader = pe.GetMetadataReader();
		var calls = new List<Call>();

		foreach (var typeHandle in reader.TypeDefinitions)
		{
			var type = reader.GetTypeDefinition(typeHandle);
			var owner = Outermost(reader, typeHandle);

			foreach (var methodHandle in type.GetMethods())
			{
				var method = reader.GetMethodDefinition(methodHandle);
				if (method.RelativeVirtualAddress == 0) continue;

				var caller = $"{FullName(reader, typeHandle)}.{reader.GetString(method.Name)}#{MetadataTokens.GetToken(methodHandle)}";
				var body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();

				if (body is null) continue;

				foreach (var (callee, at) in CalledBy(reader, body))
				{
					calls.Add(new Call(owner, caller, callee, at));
				}
			}
		}

		return calls;
	}

	/// <summary>The declared type a compiler-written one belongs to, or the type itself.</summary>
	private static string Outermost(MetadataReader reader, TypeDefinitionHandle handle)
	{
		var current = handle;

		while (true)
		{
			var type = reader.GetTypeDefinition(current);
			var declaring = type.GetDeclaringType();

			if (declaring.IsNil || !reader.GetString(type.Name).StartsWith('<')) return FullName(reader, current);

			current = declaring;
		}
	}

	private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
	{
		var type = reader.GetTypeDefinition(handle);
		var declaring = type.GetDeclaringType();
		var name = reader.GetString(type.Name);

		if (!declaring.IsNil) return $"{FullName(reader, declaring)}+{name}";

		var space = reader.GetString(type.Namespace);

		return space.Length == 0 ? name : $"{space}.{name}";
	}

	/// <summary>
	/// The methods one body calls, stepping through its instructions so an operand is never mistaken
	/// for an opcode.
	/// </summary>
	private static IEnumerable<(string Callee, int At)> CalledBy(MetadataReader reader, byte[] body)
	{
		var index = 0;
		var instruction = -1;

		while (index < body.Length)
		{
			instruction++;
			short value = body[index++];

			if (value == 0xFE) value = unchecked((short)(0xFE00 | body[index++]));

			if (!OpCodesByValue.TryGetValue(value, out var code)) yield break;

			if (code.OperandType == OperandType.InlineMethod)
			{
				var token = BitConverter.ToInt32(body, index);

				if (NameOf(reader, MetadataTokens.EntityHandle(token)) is { } callee) yield return (callee, instruction);
			}

			index += OperandSize(code, body, index);
		}
	}

	private static string? NameOf(MetadataReader reader, EntityHandle handle)
	{
		switch (handle.Kind)
		{
			case HandleKind.MethodDefinition:
			{
				var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);

				return $"{FullName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";
			}

			case HandleKind.MemberReference:
			{
				var member = reader.GetMemberReference((MemberReferenceHandle)handle);

				return $"{TypeNameOf(reader, member.Parent)}.{reader.GetString(member.Name)}";
			}

			case HandleKind.MethodSpecification:
				return NameOf(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);

			default:
				return null;
		}
	}

	private static string TypeNameOf(MetadataReader reader, EntityHandle handle)
	{
		switch (handle.Kind)
		{
			case HandleKind.TypeReference:
			{
				var type = reader.GetTypeReference((TypeReferenceHandle)handle);
				var space = reader.GetString(type.Namespace);
				var name = reader.GetString(type.Name);

				if (type.ResolutionScope.Kind == HandleKind.TypeReference)
				{
					return $"{TypeNameOf(reader, (EntityHandle)type.ResolutionScope)}+{name}";
				}

				return space.Length == 0 ? name : $"{space}.{name}";
			}

			case HandleKind.TypeDefinition:
				return FullName(reader, (TypeDefinitionHandle)handle);

			case HandleKind.TypeSpecification:
			{
				// A generic instantiation: the type it instantiates is the one the call is on.
				var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);

				if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "?";

				blob.ReadSignatureTypeCode();

				return TypeNameOf(reader, blob.ReadTypeHandle());
			}

			default:
				return "?";
		}
	}

	private static int OperandSize(OpCode code, byte[] body, int index) => code.OperandType switch
	{
		OperandType.InlineNone => 0,
		OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
		OperandType.InlineVar => 2,
		OperandType.InlineI8 or OperandType.InlineR => 8,
		OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(body, index)),
		_ => 4,
	};
}
