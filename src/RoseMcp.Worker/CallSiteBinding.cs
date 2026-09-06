using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Which parameter each argument at one call site is an argument for, asked of the compiler.
/// <para>
/// Asked rather than counted, because the order arguments are written in is not the question. A
/// named argument may appear before positional ones as long as it sits in its own parameter's place,
/// and the positional argument after it belongs to the next parameter -- so counting only the
/// positional arguments puts that one, and everything after it, on the wrong parameter. What comes
/// out of that compiles often enough to reach a build and mean something else, which is the failure
/// this whole tool exists to remove.
/// </para>
/// <para>
/// The compiler answers a second question at the same time, and it is the more important one: a call
/// site that does not bind is one where nothing at all is known about which argument means what. A
/// call that was already broken, an overload resolution that failed, an argument for a parameter the
/// member does not have yet -- each of them arrives here as no binding, and each is a call site to
/// leave exactly as written and report. Rewriting one from its text is a guess, and the guess is
/// written to disk.
/// </para>
/// <para>
/// Arguments are held as positions in the list rather than as the nodes themselves, because the list
/// being rebuilt is not always the list that was bound. A call site nested inside another one is
/// rewritten first, so by the time the outer list is rebuilt its arguments have already changed --
/// while the binding, which is a fact about what the caller wrote, was taken from the tree as it
/// stands. A position is what both lists agree on: rewriting an argument changes what is inside it
/// and never how many there are.
/// </para>
/// </summary>
public sealed record CallSiteBinding
{
	/// <summary>The three ways the compiler says an argument does not fit the parameter it was written for.</summary>
	private static readonly ImmutableHashSet<string> MappingErrors = ["CS1744", "CS1739", "CS1501"];

	/// <summary>
	/// Where the arguments for each parameter are written: the parameter's ordinal on the
	/// declaration, and the positions in the argument list of the arguments written for it. A
	/// parameter with no entry has nothing written for it here.
	/// </summary>
	public required IReadOnlyDictionary<int, IReadOnlyList<int>> ByOrdinal { get; init; }

	/// <summary>
	/// The parameter names of the method this call site actually binds to, in order. Not the same as
	/// the declaration being changed: an override is free to call its parameters something else, and
	/// a named argument has to use the names of the method it is calling.
	/// </summary>
	public required IReadOnlyList<string> ParameterNames { get; init; }

	/// <summary>
	/// How many leading parameters this call site writes no argument for, which is one for an
	/// extension method invoked on its receiver and none otherwise.
	/// </summary>
	public required int Skip { get; init; }

	/// <summary>
	/// What the call site's arguments mean, or null with the reason it cannot be said.
	/// </summary>
	/// <param name="model">The semantic model for the tree <paramref name="arguments"/> is in.</param>
	/// <param name="arguments">The argument list as it stands in that tree.</param>
	/// <param name="refusal">
	/// Why nothing can be rewritten here, as a clause naming the shape, or empty when there is a
	/// binding. It reaches the caller, so it says which call site and why rather than that something
	/// went wrong.
	/// </param>
	public static CallSiteBinding? For(SemanticModel? model, ArgumentListSyntax arguments, out string refusal)
	{
		refusal = string.Empty;

		if (model is null || arguments.Parent is not { } call)
		{
			refusal = "the call it belongs to could not be analysed";

			return null;
		}

		var operation = model.GetOperation(call);

		var bound = operation switch
		{
			IInvocationOperation invocation => invocation.Arguments,
			IObjectCreationOperation creation => creation.Arguments,
			_ => default,
		};

		var target = operation switch
		{
			IInvocationOperation invocation => invocation.TargetMethod,
			IObjectCreationOperation creation => creation.Constructor,
			_ => null,
		};

		if (bound.IsDefault || target is null)
		{
			refusal = "it does not compile as it stands, so the compiler cannot say which parameter each of its "
				+ "arguments is for. If the change just made is the one it was waiting for it may already be "
				+ "right; otherwise it was broken before this ran";

			return null;
		}

		return Read(bound, target, arguments, out refusal);
	}

	/// <summary>
	/// The errors among <paramref name="introduced"/> that say an argument does not fit the parameter
	/// it was written for, in a file where a call site was rewritten.
	/// <para>
	/// These are the ones that cannot be the caller's. CS1744, CS1739 and CS1501 are the three ways
	/// the compiler says an argument list does not match the parameters it is calling, and the
	/// argument lists in those files are the ones just written -- so an error of that shape landing
	/// there is an argument put on the wrong parameter, which is the one failure a signature change
	/// exists to prevent and the one a caller would spend an afternoon looking for in code they did
	/// not write.
	/// </para>
	/// <para>
	/// Matched by file rather than by span, because the errors are read from the solution after the
	/// edit and the call sites are known from the solution before it. The file is enough: the error
	/// has to be new, and the call sites rewritten in it are the only argument lists that changed.
	/// </para>
	/// </summary>
	/// <param name="rewrittenFiles">Paths of the files a call site was rewritten in.</param>
	/// <param name="introduced">Errors that exist now and did not before.</param>
	public static IReadOnlyList<DiagnosticEntry> MappingFailures(
		IEnumerable<string> rewrittenFiles,
		IEnumerable<DiagnosticEntry> introduced)
	{
		var rewritten = rewrittenFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (rewritten.Count == 0) return [];

		bool Mapping(DiagnosticEntry diagnostic) =>
			MappingErrors.Contains(diagnostic.Id)
				&& diagnostic.FilePath is { Length: > 0 } path
				&& rewritten.Contains(path);

		return [.. introduced.Where(Mapping)];
	}

	/// <summary>The binding itself, once there is a method to bind against.</summary>
	private static CallSiteBinding? Read(
		ImmutableArray<IArgumentOperation> bound,
		IMethodSymbol target,
		ArgumentListSyntax arguments,
		out string refusal)
	{
		refusal = string.Empty;

		var byOrdinal = new Dictionary<int, IReadOnlyList<int>>();
		var skip = 0;

		foreach (var argument in bound)
		{
			if (argument.Parameter is not { } parameter)
			{
				refusal = "one of its arguments is for no parameter at all";

				return null;
			}

			// An optional the call site says nothing about. Nothing is written for it, so there is
			// nothing to move and it goes on saying nothing about it.
			if (argument.ArgumentKind == ArgumentKind.DefaultValue) continue;

			if (argument.ArgumentKind == ArgumentKind.ParamArray)
			{
				if (Expanded(argument, arguments) is not { } expansion)
				{
					refusal = $"the arguments it passes for '{parameter.Name}' are expanded in a shape this "
						+ "cannot take apart";

					return null;
				}

				if (expansion.Count > 0) byOrdinal[parameter.Ordinal] = expansion;

				continue;
			}

			if (argument.Syntax is ArgumentSyntax written)
			{
				byOrdinal[parameter.Ordinal] = [arguments.Arguments.IndexOf(written)];

				continue;
			}

			// The receiver of an extension method invoked on it. The compiler binds it to the first
			// parameter, and it is written before the argument list rather than inside it -- so that
			// parameter has no argument here to move, and none may be written for it either.
			if (target.IsExtensionMethod && parameter.Ordinal == 0)
			{
				skip++;

				continue;
			}

			refusal = $"its argument for '{parameter.Name}' is not written anywhere this can move it from";

			return null;
		}

		return new CallSiteBinding
		{
			ByOrdinal = byOrdinal,
			ParameterNames = [.. target.Parameters.Select(parameter => parameter.Name)],
			Skip = skip,
		};
	}

	/// <summary>
	/// The positions of the arguments a params expansion wrote, or null when the expansion is not one
	/// this can read. The compiler models an expansion as the array it would build, so the arguments
	/// themselves are the elements of that array's initialiser rather than arguments of the call.
	/// </summary>
	private static IReadOnlyList<int>? Expanded(IArgumentOperation argument, ArgumentListSyntax arguments)
	{
		if (argument.Value is not IArrayCreationOperation creation) return null;

		// An expansion of nothing at all: the parameter takes no argument here, which is not the same
		// as an expansion this cannot read.
		if (creation.Initializer is not { } initializer) return [];

		var written = new List<int>(initializer.ElementValues.Length);

		foreach (var element in initializer.ElementValues)
		{
			if (element.Syntax.Parent is not ArgumentSyntax argumentSyntax) return null;

			written.Add(arguments.Arguments.IndexOf(argumentSyntax));
		}

		return written;
	}
}
