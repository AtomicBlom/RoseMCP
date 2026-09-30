using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The tool surface as the assembly declares it: names, methods and argument names.
/// <para>
/// Read from the assembly rather than from the registration, because the registration gates the
/// live-app half by operating system and the rules asserted against this hold on every platform.
/// <c>[McpServerToolType]</c> is what the SDK itself scans, so a tool class added later is covered
/// without any test naming it.
/// </para>
/// </summary>
internal static class DeclaredSurface
{
	/// <summary>Every tool the broker declares, with the method that implements it.</summary>
	internal static IEnumerable<(string Name, MethodInfo Method)> Tools()
	{
		var types = typeof(WorkspaceManager).Assembly
			.GetTypes()
			.Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

		foreach (var type in types)
		{
			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				var tool = method.GetCustomAttribute<McpServerToolAttribute>();
				if (tool?.Name is not null) yield return (tool.Name, method);
			}
		}
	}

	/// <summary>
	/// Every name a caller can put in an arguments object, across the whole surface. Compared
	/// case-insensitively by callers, because an argument is camel-cased on the wire and a property
	/// it corresponds to is not.
	/// </summary>
	internal static HashSet<string> ArgumentNames() =>
	[
		.. Tools()
			.SelectMany(tool => tool.Method.GetParameters())
			.Select(parameter => parameter.Name)
			.OfType<string>(),
	];

	/// <summary>What a tool answers with, with the <see cref="Task{T}"/> unwrapped.</summary>
	internal static Type Returned(MethodInfo method)
	{
		var returned = method.ReturnType;

		return returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(Task<>)
			? returned.GetGenericArguments()[0]
			: returned;
	}
}
