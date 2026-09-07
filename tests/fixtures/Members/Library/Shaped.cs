using System;

namespace Library;

/// <summary>
/// The call sites a signature change reaches that are not invocations: a name written without a
/// call, and a delegate made out of the method rather than a call to it.
/// <para>
/// Both compile today and one of them stops compiling the moment the signature moves, which is why
/// they have to be reported rather than passed over. Nothing can rewrite either: a nameof carries no
/// arguments to put back, and a method group's shape is decided by the delegate type it converts to.
/// </para>
/// </summary>
public static class Shaped
{
	/// <summary>The member the shape cases change.</summary>
	public static string Combine(string first, string second) => first + second;

	/// <summary>Names it without calling it.</summary>
	public static string Names() => nameof(Combine);

	/// <summary>Converts it to a delegate, which the added parameter breaks.</summary>
	public static Func<string, string, string> Group() => Combine;
}

/// <summary>
/// A constructor reached through an initialiser rather than through an invocation, which is a real
/// call with real arguments and no InvocationExpression anywhere in it.
/// </summary>
public class Rooted
{
	/// <summary>The one the shape cases change.</summary>
	protected Rooted(string name)
	{
		Name = name;
	}

	/// <summary>Calls the one above through a this-initialiser.</summary>
	protected Rooted()
		: this("none")
	{
	}

	public string Name { get; }
}

/// <summary>Calls the base constructor through a base-initialiser.</summary>
public sealed class Grown : Rooted
{
	/// <summary>Passes the name straight through.</summary>
	public Grown(string name)
		: base(name)
	{
	}
}
