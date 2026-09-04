using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Rewrites one call site's arguments for a changed parameter list, or says it cannot.
/// <para>
/// The arguments are moved rather than regenerated: each one is the caller's own
/// <see cref="ArgumentSyntax"/> with its name colon added or taken off, so a <c>ref</c>, an
/// <c>out var</c>, a comment written beside it and the exact spelling of the expression all survive
/// a change that has nothing to do with them.
/// </para>
/// <para>
/// It returns null rather than guessing. A call site it cannot rewrite is reported and left alone,
/// which leaves the caller with a compile error they were told about, in a place they were pointed
/// at -- and that is much better than a plausible rewrite that binds an argument to the wrong
/// parameter, which is the failure with no symptom.
/// </para>
/// </summary>
public static class CallSiteRewriter
{
	/// <summary>
	/// The new argument list, or null when this call site has to be left to a person.
	/// </summary>
	/// <param name="arguments">The arguments as written.</param>
	/// <param name="plan">What is happening to the parameters.</param>
	/// <param name="supplied">Expressions to pass for new parameters, by parameter name.</param>
	/// <param name="skip">
	/// Parameters the call site does not write an argument for: one, for an extension method invoked
	/// on its receiver, and none otherwise.
	/// </param>
	public static ArgumentListSyntax? Rewrite(
		ArgumentListSyntax arguments,
		ParameterPlan plan,
		IReadOnlyDictionary<string, string> supplied,
		int skip)
	{
		var byOldIndex = Match(arguments, plan, skip);
		if (byOldIndex is null) return null;

		var emitted = new List<ArgumentSyntax>();
		var allPositionalSoFar = true;

		foreach (var parameter in plan.Parameters.Skip(skip))
		{
			var slot = parameter.IsAt - skip;

			if (!TryArgumentsFor(parameter, byOldIndex, supplied, out var wanted)) return null;
			if (wanted.Count == 0) continue;

			// A positional argument only stays positional while it would land in its own slot, and
			// only until something has had to be named: C# will not take a positional argument after
			// a named one.
			var positional = allPositionalSoFar && slot == emitted.Count;

			// Several arguments for one parameter is a params expansion, and there is no way to write
			// that as a named argument at all.
			if (wanted.Count > 1 && !positional) return null;

			foreach (var argument in wanted)
			{
				emitted.Add(positional ? argument.WithNameColon(null) : Named(parameter.Name, argument));
			}

			if (!positional) allPositionalSoFar = false;
		}

		return arguments.WithArguments(SyntaxFactory.SeparatedList(emitted, Separators(emitted.Count, arguments)));
	}

	/// <summary>
	/// The commas, keeping the ones already at this call site so an argument list somebody wrapped
	/// across lines stays wrapped, and using a comma and a space for any the list has gained.
	/// <para>
	/// Worth the trouble: a separated list built without them renders <c>Foo("a",false)</c>, which is
	/// valid C# and fails IDE0055 in any repository with an opinion about the space -- the exact class
	/// of failure these tools exist to remove.
	/// </para>
	/// </summary>
	private static IEnumerable<SyntaxToken> Separators(int count, ArgumentListSyntax existing)
	{
		var already = existing.Arguments.GetSeparators().ToArray();

		for (var index = 0; index < count - 1; index++)
		{
			yield return index < already.Length
				? already[index]
				: SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);
		}
	}

	/// <summary>
	/// Each old parameter's argument, by the index the parameter had. Null when the call site says
	/// something this cannot read.
	/// </summary>
	private static Dictionary<int, List<ArgumentSyntax>>? Match(
		ArgumentListSyntax arguments,
		ParameterPlan plan,
		int skip)
	{
		var byName = plan.Parameters
			.Where(parameter => parameter.WasAt is not null)
			.ToDictionary(parameter => parameter.Name, parameter => parameter.WasAt!.Value, StringComparer.Ordinal);

		var matched = new Dictionary<int, List<ArgumentSyntax>>();
		var position = 0;
		var lastParams = plan.Parameters.FirstOrDefault(parameter => parameter.WasParams);

		foreach (var argument in arguments.Arguments)
		{
			if (argument.NameColon is { } name)
			{
				// A named argument for a parameter that is going: it has nowhere to go, and dropping
				// it is exactly what removing the parameter means.
				if (!byName.TryGetValue(name.Name.Identifier.Text, out var index))
				{
					if (plan.Removed.Contains(name.Name.Identifier.Text, StringComparer.Ordinal)) continue;

					return null;
				}

				matched[index] = [argument];
				continue;
			}

			var slot = position + skip;
			position++;

			// Past the end of the parameter list, the extras belong to the params parameter -- which
			// is the only way there can be more arguments than parameters.
			if (lastParams?.WasAt is { } at && slot > at) slot = at;

			if (!matched.TryGetValue(slot, out var existing)) matched[slot] = existing = [];

			existing.Add(argument);
		}

		return matched;
	}

	/// <summary>
	/// The arguments to write for one parameter: the ones it already had, the one the caller
	/// supplied for it, or none when it is new and optional.
	/// </summary>
	private static bool TryArgumentsFor(
		PlannedParameter parameter,
		Dictionary<int, List<ArgumentSyntax>> byOldIndex,
		IReadOnlyDictionary<string, string> supplied,
		out IReadOnlyList<ArgumentSyntax> wanted)
	{
		if (parameter.WasAt is { } at)
		{
			// Nothing at this call site for a parameter that has one is an omitted optional, and it
			// stays omitted.
			wanted = byOldIndex.TryGetValue(at, out var existing) ? existing : [];
			return true;
		}

		if (supplied.TryGetValue(parameter.Name, out var expression))
		{
			wanted = [SyntaxFactory.Argument(SyntaxFactory.ParseExpression(expression))];
			return true;
		}

		// New and optional: every call site can go on saying nothing about it, which is the whole
		// reason to give a new parameter a default.
		wanted = [];

		return parameter.HasDefault;
	}

	private static ArgumentSyntax Named(string name, ArgumentSyntax argument) =>
		argument.WithNameColon(
			SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name))
				.WithTrailingTrivia(SyntaxFactory.Space));
}
