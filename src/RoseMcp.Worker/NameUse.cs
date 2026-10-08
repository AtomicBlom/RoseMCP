using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// How the code uses a name that did not bind, which decides what kind of symbol an import could
/// bring in for it.
/// <para>
/// The kind matters because "the only namespace anything of that name is in" is true and answers the
/// wrong question when the anything is the wrong kind of thing. <c>Group(call, name)</c> calls
/// <c>Group</c>; the only <c>Group</c> a project sees is a type in
/// <c>System.Text.RegularExpressions</c>; importing it turns one CS0103 into a CS1955 and an unused
/// using, in a line the caller never wrote. A type is only ever invoked as <c>new Group(...)</c>.
/// </para>
/// </summary>
public enum NameUse
{
	/// <summary>Anywhere a type or a value could stand, which is what the code does not say.</summary>
	Any,

	/// <summary>Where only a type can stand: a declaration's type, <c>new T()</c>, <c>typeof(T)</c>.</summary>
	Type,

	/// <summary>
	/// Called on its own, as <c>Group(...)</c>. Only a method or something of delegate type is called,
	/// and a namespace import brings neither into reach of a bare call: a type is not invoked, and an
	/// extension method needs a receiver.
	/// </summary>
	Invoked,

	/// <summary>
	/// After a dot, as <c>text.Shouted</c> or <c>text.Shouted()</c>. The receiver is already known, so
	/// what an import can add is an extension, never a type.
	/// </summary>
	Member,
}

/// <summary>Reads a <see cref="NameUse"/> off the syntax around an unresolved name.</summary>
public static class NameUses
{
	/// <summary>How the name <paramref name="token"/> is part of is used.</summary>
	public static NameUse Of(SyntaxToken token)
	{
		if (token.Parent is not SimpleNameSyntax name) return NameUse.Any;

		ExpressionSyntax used = name;
		var afterDot = false;

		switch (name.Parent)
		{
			case MemberAccessExpressionSyntax access when access.Name == name:
				used = access;
				afterDot = true;
				break;

			case MemberBindingExpressionSyntax binding when binding.Name == name:
				used = binding;
				afterDot = true;
				break;
		}

		var invoked = used.Parent is InvocationExpressionSyntax invocation && invocation.Expression == used;

		if (afterDot) return NameUse.Member;
		if (invoked) return NameUse.Invoked;

		return SyntaxFacts.IsInTypeOnlyContext(name) ? NameUse.Type : NameUse.Any;
	}

	/// <summary>Whether a type could answer a name used this way.</summary>
	public static bool TakesAType(this NameUse use) => use is NameUse.Any or NameUse.Type;

	/// <summary>Whether an extension method could answer a name used this way.</summary>
	public static bool TakesAnExtension(this NameUse use) => use is NameUse.Any or NameUse.Member;

	/// <summary>
	/// Why nothing was imported for a name used this way, where its use ruled out everything the
	/// search could have offered; null where it ruled out nothing.
	/// </summary>
	public static string? WhyNothingFits(this NameUse use, string name) => use switch
	{
		NameUse.Invoked => $"{name} is called here on its own, and nothing an import brings in can be: a type is "
			+ $"not invoked, and an extension method needs a receiver. Declare {name}, or qualify the call.",
		NameUse.Type => $"{name} is used as a type here, and no type of that name is in reach, so no import would "
			+ "fix it.",
		NameUse.Member => $"{name} is used after a dot here, and no extension method of that name is in reach, so "
			+ "no import would fix it.",
		_ => null,
	};
}
