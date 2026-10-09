using System.Diagnostics;
using System.Text.Json;

namespace RoseMcp.Contracts;

/// <summary>
/// Tells a refusal a tool wrote from a framework's exception that escaped it, and words the second so
/// that it cannot be read as the first.
/// <para>
/// Both reach the boundary as the same CLR types. Rose refuses with <see cref="ArgumentException"/> and
/// <see cref="InvalidOperationException"/>, and so do Roslyn, MSBuild and the BCL, so a message forwarded
/// as it stands reads as a considered refusal whichever of them wrote it -- and a framework's says what
/// went wrong somewhere else, in a vocabulary the caller cannot act on. "Parameter 'symbol' must be a
/// symbol from this compilation" reached a caller looking exactly like advice about the argument they
/// sent.
/// </para>
/// <para>
/// Told apart by where the exception was thrown rather than by its type, because that needs nothing of
/// the throw sites: the deepest frame that is not a throw helper says whose code decided to throw. Code
/// in a RoseMcp assembly throwing is a refusal, written for the caller and forwarded verbatim -- unless
/// it is a fault the runtime raised, a null dereference or a bad cast, which is Rose failing. The MCP
/// SDK's binder throwing is a refusal too -- an argument missing or the wrong shape -- and so is any
/// <see cref="JsonException"/>, which the boundary already explains from the schema. An I/O failure is
/// a fact about the machine that names the path involved, usually one the caller sent, so it stands as
/// it is. Anything else is a framework exception that escaped a tool: it keeps its message, says which
/// tool and which component it came from, and loses its parameter name, which is never one of the
/// tool's arguments.
/// </para>
/// <para>
/// A marker type every deliberate refusal derives from would say the same thing, at the cost of every
/// throw site in three hosts and of the tests that assert on <see cref="ArgumentException"/>; and the
/// throw site that forgot it would be called a fault, so the rule would still rest on a reviewer
/// remembering it. Where the throw happened is known for every exception without anyone remembering
/// anything.
/// </para>
/// </summary>
public static class ToolFailure
{
	/// <summary>
	/// What to tell the caller about an exception a tool threw: its own message where it is a refusal,
	/// and where it escaped a framework, that message framed as the fault it is.
	/// </summary>
	/// <param name="exception">What the tool threw.</param>
	/// <param name="tool">The tool's name, as the caller called it.</param>
	public static string Message(Exception exception, string tool) =>
		LeakedFrom(exception) is { } assembly
			? Leaked(tool, Component(assembly), exception.Message)
			: exception.Message;

	/// <summary>
	/// The assembly a failure escaped from, or null where the exception is a refusal: one thrown by Rose's
	/// own code, by the MCP SDK's argument binder, or a JSON or I/O failure, whose message is already about
	/// what the caller sent.
	/// <para>
	/// A fault the runtime raises is never a refusal, wherever it was raised: a null dereference, an index
	/// past an array's end, a bad cast, an arithmetic fault or a member nobody wrote. Nobody throws one of
	/// those to tell a caller something, so one raised inside Rose's own code is Rose failing, and is framed
	/// as that rather than forwarded as "Object reference not set to an instance of an object". A
	/// <see cref="KeyNotFoundException"/> is left to the frame: from a dictionary's indexer the frame is the
	/// BCL's and it is already a leak, while one Rose throws on purpose is a refusal like any other.
	/// </para>
	/// </summary>
	public static string? LeakedFrom(Exception exception)
	{
		if (exception is JsonException or IOException or UnauthorizedAccessException) return null;

		if (IsRuntimeFault(exception)) return FaultingAssembly(exception) ?? "RoseMcp";

		var assembly = ThrowingAssembly(exception);
		if (assembly is null) return null;

		var isRefusal = IsRose(assembly) || IsBinder(assembly);

		return isRefusal ? null : assembly;
	}

	/// <summary>
	/// The name of the assembly whose code threw, read from the deepest frame that is not a throw helper;
	/// null for an exception with no frames to read, which was never thrown.
	/// <para>
	/// A throw helper is passed over because it throws on its caller's behalf:
	/// <c>ArgumentNullException.ThrowIfNull</c> and the BCL's <c>ThrowHelper</c> are where the throw
	/// happens, and the code that called them is what decided to throw.
	/// </para>
	/// </summary>
	public static string? ThrowingAssembly(Exception exception)
	{
		foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
		{
			var type = frame.GetMethod()?.DeclaringType;
			if (type is null || IsThrowHelper(type)) continue;

			return type.Assembly.GetName().Name;
		}

		return null;
	}

	/// <summary>
	/// The assembly whose code hit a runtime fault: the deepest frame outside the runtime's own library, or
	/// the throwing one where every frame is inside it.
	/// <para>
	/// The runtime raises a bad unboxing cast from a helper of its own, and a null or an index fault inside a
	/// collection is reached through code that was handed the bad value. Either way the code at fault is the
	/// first caller outside the runtime, which is what the caller should be told failed.
	/// </para>
	/// </summary>
	private static string? FaultingAssembly(Exception exception)
	{
		foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
		{
			var type = frame.GetMethod()?.DeclaringType;
			if (type is null || IsThrowHelper(type)) continue;

			var name = type.Assembly.GetName().Name;
			if (name is not "System.Private.CoreLib") return name;
		}

		return ThrowingAssembly(exception);
	}

	/// <summary>
	/// A framework's message framed as the fault it is: the tool, the component the exception came from,
	/// and its own words without any parameter name.
	/// <para>
	/// The message is kept because it is the only account there is of what failed, and a caller reporting
	/// the fault needs it. The parameter name goes, and the sentence after the message says why, since a
	/// framework's message can name its parameter in its prose too, where no rewrite can reach it.
	/// </para>
	/// </summary>
	/// <param name="tool">The tool's name, as the caller called it.</param>
	/// <param name="component">The component the exception escaped from, as <see cref="Component"/> names it.</param>
	/// <param name="message">The exception's own message.</param>
	public static string Leaked(string tool, string component, string message)
	{
		var said = ToolArgumentShape.WithoutParameterNames(message).TrimEnd();
		var endsASentence = said.Length > 0 && ".?!".Contains(said[^1]);
		var sentence = endsASentence ? said : $"{said}.";

		return $"`{tool}` failed inside {component} rather than refusing the call. {component} said: {sentence} "
			+ $"Any parameter it names is {component}'s own, not an argument of `{tool}`; if the arguments sent "
			+ "are right, this is a fault in Rose worth reporting.";
	}

	/// <summary>
	/// What to call the component an assembly belongs to, in words a caller knows: Rose, Roslyn, MSBuild and
	/// .NET by those names, anything else by its assembly name.
	/// </summary>
	public static string Component(string assembly)
	{
		if (IsRose(assembly)) return "Rose";
		if (assembly.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)) return "Roslyn";
		if (assembly.StartsWith("Microsoft.Build", StringComparison.Ordinal)) return "MSBuild";

		var isRuntime = assembly is "System.Private.CoreLib" or "mscorlib" or "netstandard" or "System"
			|| assembly.StartsWith("System.", StringComparison.Ordinal);

		return isRuntime ? ".NET" : assembly;
	}

	/// <summary>Whether an assembly is one of Rose's own, whose throws are refusals written for a caller.</summary>
	private static bool IsRose(string assembly) =>
		assembly.Equals("RoseMcp", StringComparison.Ordinal)
		|| assembly.StartsWith("RoseMcp.", StringComparison.Ordinal);

	/// <summary>
	/// Whether an exception is a fault the runtime raises rather than one code throws to say something: these
	/// mean the code that hit them is wrong, whoever wrote it.
	/// </summary>
	private static bool IsRuntimeFault(Exception exception) =>
		exception is NullReferenceException
			or IndexOutOfRangeException
			or InvalidCastException
			or ArithmeticException
			or ArrayTypeMismatchException
			or NotImplementedException;

	/// <summary>
	/// Whether an assembly is the MCP SDK's or the function binder beneath it, which throw when the
	/// arguments a caller sent cannot be bound: a required one missing, or one of the wrong shape.
	/// </summary>
	private static bool IsBinder(string assembly) =>
		assembly.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
		|| assembly.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal);

	/// <summary>
	/// Whether a frame's type only throws on behalf of its caller: an exception type's own static
	/// helpers, or a type the BCL names <c>ThrowHelper</c>.
	/// </summary>
	private static bool IsThrowHelper(Type type) =>
		typeof(Exception).IsAssignableFrom(type)
		|| type.Name.EndsWith("ThrowHelper", StringComparison.Ordinal);
}
