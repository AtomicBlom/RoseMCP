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
/// Which argument belongs to which parameter is not worked out here. It is read off a
/// <see cref="CallSiteBinding"/>, which is the compiler's own answer -- because the order arguments
/// are written in is not the same question, and a call site that cannot be bound at all is one
/// where nothing is known about what its arguments mean.
/// </para>
/// <para>
/// It returns null rather than guessing, with the reason. A call site it cannot rewrite is reported
/// and left alone, which leaves the caller with a compile error they were told about, in a place
/// they were pointed at -- and that is much better than a plausible rewrite that binds an argument
/// to the wrong parameter, which is the failure with no symptom.
/// </para>
/// </summary>
public static class CallSiteRewriter
{
	/// <summary>
	/// The new argument list, or null when this call site has to be left to a person.
	/// </summary>
	/// <param name="arguments">The arguments as they now stand, inner call sites already rewritten.</param>
	/// <param name="binding">Which parameter each of those arguments is an argument for.</param>
	/// <param name="plan">What is happening to the parameters.</param>
	/// <param name="supplied">Expressions to pass for new parameters, by parameter name.</param>
	/// <param name="refusal">
	/// Why this call site is being left, as a clause naming the shape, or empty when it was
	/// rewritten. It reaches the caller, who has to decide what to do about the site.
	/// </param>
	public static ArgumentListSyntax? Rewrite(
		ArgumentListSyntax arguments,
		CallSiteBinding binding,
		ParameterPlan plan,
		IReadOnlyDictionary<string, string> supplied,
		out string refusal)
	{
		refusal = string.Empty;

		var emitted = new List<ArgumentSyntax>();
		var allPositionalSoFar = true;

		foreach (var parameter in plan.Parameters.Skip(binding.Skip))
		{
			var slot = parameter.IsAt - binding.Skip;

			if (!TryArgumentsFor(parameter, arguments, binding, supplied, out var wanted))
			{
				refusal = $"nothing is passed for '{parameter.Name}' here and the new declaration gives it no default";

				return null;
			}

			if (wanted.Count == 0) continue;

			// A positional argument only stays positional while it would land in its own slot, and
			// only until something has had to be named: C# will not take a positional argument after
			// a named one.
			var positional = allPositionalSoFar && slot == emitted.Count;

			// Several arguments for one parameter is a params expansion, and there is no way to write
			// that as a named argument at all.
			if (wanted.Count > 1 && !positional)
			{
				refusal = $"the arguments it passes for '{parameter.Name}' are a params expansion, and this change "
					+ "would need them written as a named argument, which C# has no way to spell";

				return null;
			}

			foreach (var argument in wanted)
			{
				// A name the caller wrote stays. Keeping it reproduces the text already in the file, so
				// the site comes out unchanged; taking it off is an edit to a call site that needed
				// none, and it takes the only thing saying what a bare null or true is for. An argument
				// only ever arrives named for a parameter the member already had, since a new
				// parameter's argument is built here and carries no name -- so nothing is left that a
				// name has to come off. A name kept in its own position is legal beside the positional
				// arguments after it, which C# has allowed since 7.2.
				var named = !positional || argument.NameColon is not null;

				emitted.Add(named ? Named(NameFor(parameter, binding), argument) : argument);
			}

			if (!positional) allPositionalSoFar = false;
		}

		return arguments.WithArguments(SyntaxFactory.SeparatedList(emitted, Separators(emitted.Count, arguments)));
	}

	/// <summary>
	/// The commas, keeping the ones already at this call site and giving any the list has gained the
	/// shape of the last one that was there.
	/// <para>
	/// Worth the trouble twice over. A separated list built without them renders <c>Foo("a",false)</c>,
	/// which is valid C# and fails IDE0055 in any repository with an opinion about the space -- the
	/// exact class of failure these tools exist to remove. And a comma and a space is only right for a
	/// list written on one line: in a wrapped one it leaves the argument after it up on the line
	/// above, behind a trailing space, which nothing reports because a continuation line is not a
	/// statement.
	/// </para>
	/// </summary>
	private static IEnumerable<SyntaxToken> Separators(int count, ArgumentListSyntax existing)
	{
		var already = existing.Arguments.GetSeparators().ToArray();

		var gained = already.Length > 0
			? already[^1]
			: SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

		for (var index = 0; index < count - 1; index++)
		{
			yield return index < already.Length ? already[index] : gained;
		}
	}

	/// <summary>
	/// The whitespace in front of an argument that sits on a line of its own here, or none where the
	/// call site is written on one line.
	/// <para>
	/// Read from the arguments rather than worked out, because it is the only thing at hand that
	/// knows how deep this particular call is indented -- and the line break belongs to the comma
	/// before it, so what is left on the argument is the indentation alone.
	/// </para>
	/// </summary>
	private static SyntaxTriviaList Continuation(ArgumentListSyntax arguments)
	{
		foreach (var argument in arguments.Arguments)
		{
			var leading = argument.GetLeadingTrivia();

			if (leading.Count > 0) return leading;
		}

		return default;
	}

	/// <summary>
	/// The arguments to write for one parameter: the ones already written for it here, the one the
	/// caller supplied for it, or none when it is new and optional.
	/// </summary>
	private static bool TryArgumentsFor(
		PlannedParameter parameter,
		ArgumentListSyntax arguments,
		CallSiteBinding binding,
		IReadOnlyDictionary<string, string> supplied,
		out IReadOnlyList<ArgumentSyntax> wanted)
	{
		if (parameter.WasAt is { } at)
		{
			// Nothing written here for a parameter that exists is an omitted optional, and it stays
			// omitted. Taken by position rather than by node, so an argument that is itself a call
			// site this pass has already rewritten comes through rewritten.
			wanted = binding.ByOrdinal.TryGetValue(at, out var written)
				? [.. written.Select(index => arguments.Arguments[index])]
				: [];

			return true;
		}

		if (supplied.TryGetValue(parameter.Name, out var expression))
		{
			// Given the indentation the arguments already here have, so an argument arriving in the
			// middle of a call site somebody wrapped by hand lands on a line of its own rather than
			// at column zero.
			wanted =
			[
				SyntaxFactory.Argument(SyntaxFactory.ParseExpression(expression))
					.WithLeadingTrivia(Continuation(arguments)),
			];

			return true;
		}

		// New and optional: every call site can go on saying nothing about it, which is the whole
		// reason to give a new parameter a default.
		wanted = [];

		return parameter.HasDefault;
	}

	/// <summary>
	/// The name to write when an argument has to be named. Taken from the method the call site binds
	/// to rather than from the declaration being changed, because an override is free to call its
	/// parameters something else and a named argument has to use the names of the method it calls --
	/// so naming it from the declaration is CS1739 at every call site reached through an override
	/// that renamed anything. A parameter that is new has one name everywhere, since every
	/// declaration takes it from what the caller wrote.
	/// </summary>
	private static string NameFor(PlannedParameter parameter, CallSiteBinding binding) =>
		parameter.WasAt is { } at && at < binding.ParameterNames.Count
			? binding.ParameterNames[at]
			: parameter.Name;

	/// <summary>
	/// The argument with a name colon put on, or kept, and the whitespace in front of it still in
	/// front of it.
	/// <para>
	/// An argument's leading trivia sits on its first token, and naming one puts a new token in
	/// front. Left where it was, the line break and the indentation end up between the name and the
	/// value -- <c>filePath: \t\t\tTestContext.Current.CancellationToken</c>, which is what the
	/// finding behind the binding work reported alongside the wrong parameter. Getting the parameter
	/// right did not move it.
	/// </para>
	/// <para>
	/// The same trivia goes the same way from the other side, which is why nothing here takes a name
	/// off: for a named argument the break and the indentation sit on the name, so removing the colon
	/// takes them with it and the argument lands at column zero -- what four call sites of one wrapped
	/// method did the moment their arguments no longer needed naming. A name the caller wrote is kept
	/// instead, so that path does not exist.
	/// </para>
	/// </summary>
	private static ArgumentSyntax Named(string name, ArgumentSyntax argument)
	{
		var leading = argument.GetLeadingTrivia();

		var colon = SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name))
			.WithTrailingTrivia(SyntaxFactory.Space);

		return argument
			.WithExpression(argument.Expression.WithoutLeadingTrivia())
			.WithNameColon(colon)
			.WithLeadingTrivia(leading);
	}
}
