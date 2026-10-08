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

		// Read before anything is taken apart: the break in front of a closing parenthesis on its own line
		// can be the only one the list has, and it is lifted off the last argument below.
		var lineBreak = LineBreakIn(arguments);

		arguments = WithInlineCommentsOnTheirArgument(arguments);
		arguments = WithoutClosingLine(arguments, out var closingComment, out var closingLayout);

		var emitted = new List<ArgumentSyntax>();

		// Where each emitted argument stood in the list as written, or -1 for one that is new. An
		// argument still in its own position keeps the whitespace in front of it untouched.
		var origins = new List<int>();
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

				origins.Add(arguments.Arguments.IndexOf(argument));
				emitted.Add(named ? Named(NameFor(parameter, binding), argument) : argument);
			}

			if (!positional) allPositionalSoFar = false;
		}

		if (LosesDirective(arguments, origins))
		{
			refusal = "a preprocessor directive is written in front of an argument or comma this change takes out, "
				+ "and taking it out would leave the directive's block unbalanced";

			return null;
		}

		var comments = LineEndComments(arguments, closingComment);
		var separators = Separators(origins, arguments, comments, lineBreak).ToArray();
		var indentation = Indentation(arguments);

		var laidOut = emitted
			.Select((argument, index) => origins[index] == index
				? argument
				: InSlot(argument, index == 0 ? arguments.OpenParenToken : separators[index - 1], indentation, lineBreak))
			.ToList();

		if (laidOut.Count > 0)
		{
			var comment = origins[^1] < 0 ? default : comments[origins[^1]];

			laidOut[^1] = Closing(laidOut[^1], comment, closingLayout, arguments, indentation, lineBreak);
		}

		return arguments.WithArguments(SyntaxFactory.SeparatedList(laidOut, separators));
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
	/// <para>
	/// A comma's position decides its layout, and the argument in front of it decides its comment. A
	/// comment ending the line after a comma is about the argument before it, so it goes wherever that
	/// argument goes: a comma copied whole would write it a second time beside an argument it does not
	/// describe, and one dropped with the position would delete it without a word.
	/// </para>
	/// </summary>
	/// <param name="origins">Where each emitted argument stood in the list as written, or -1 for one that is new.</param>
	/// <param name="existing">The list as written.</param>
	/// <param name="comments">The comment that ended the line after each argument as written, by its position there.</param>
	/// <param name="lineBreak">The call site's own line ending, for a comma a carried line comment has to end.</param>
	private static IEnumerable<SyntaxToken> Separators(
		IReadOnlyList<int> origins,
		ArgumentListSyntax existing,
		SyntaxTriviaList[] comments,
		SyntaxTrivia lineBreak)
	{
		var already = existing.Arguments.GetSeparators().ToArray();

		var gained = already.Length > 0
			? already[^1].WithTrailingTrivia(Layout(already[^1]))
			: SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

		for (var index = 0; index < origins.Count - 1; index++)
		{
			var comma = index < already.Length ? already[index].WithTrailingTrivia(Layout(already[index])) : gained;

			var comment = origins[index] < 0 ? default : comments[origins[index]];

			yield return comma.WithTrailingTrivia(EndingLineComment(comment.Concat(comma.TrailingTrivia), lineBreak, out _));
		}
	}

	/// <summary>
	/// The indentation of an argument that begins a line at this call site, or none where no argument
	/// does.
	/// <para>
	/// Read from the arguments rather than worked out, because it is the only thing at hand that
	/// knows how deep this particular call is indented. The line break belongs to the token before
	/// the argument, so what is left on the argument is any blank lines and comment lines the caller
	/// put above it, and then the indentation of its own line -- which is the whitespace after the last
	/// break, up to whatever comment the caller wrote in front of it on that line.
	/// </para>
	/// </summary>
	private static SyntaxTriviaList Indentation(ArgumentListSyntax arguments)
	{
		for (var index = 0; index < arguments.Arguments.Count; index++)
		{
			var before = index == 0 ? arguments.OpenParenToken : arguments.Arguments.GetSeparator(index - 1);

			if (EndsLine(before))
			{
				return [.. AfterLastBreak(arguments.Arguments[index].GetLeadingTrivia()).TakeWhile(IsWhitespace)];
			}
		}

		return InsideClosingParenthesis(arguments);
	}

	/// <summary>
	/// An argument laid out for the position it now takes: at the call site's indentation where the
	/// token in front of it ends a line, and with no whitespace of its own where it does not.
	/// <para>
	/// Indentation belongs to the line an argument begins, not to the argument, so a new argument or
	/// one that has moved cannot bring it along. Taking the indentation of an argument that begins a
	/// line is right where every argument begins one, and wrong where several share a continuation
	/// line: there the new argument lands after a comma and a space, and the indentation becomes a run
	/// of tabs in the middle of the line -- which compiles, and is exactly the argument list the change
	/// asked for, so nothing reports it.
	/// </para>
	/// <para>
	/// Only layout is replaced. A comment the caller wrote before the argument goes where the argument
	/// goes, and so does a blank line above one that still begins a line, with the indentation after it
	/// that is already its own. Mid-line, the whitespace and breaks in front of it are dropped, since a
	/// break there would strand a trailing space after the comma.
	/// </para>
	/// </summary>
	private static ArgumentSyntax InSlot(
		ArgumentSyntax argument,
		SyntaxToken before,
		SyntaxTriviaList indentation,
		SyntaxTrivia lineBreak)
	{
		var leading = argument.GetLeadingTrivia();

		if (!EndsLine(before))
		{
			var written = leading.SkipWhile(IsLayout).ToArray();

			// A directive has to begin its line, so one that would now follow the comma keeps a break in
			// front of it; without one it is CS1040.
			var opensWithDirective = written.Length > 0 && written[0].IsDirective;

			return argument.WithLeadingTrivia(opensWithDirective ? written.Prepend(lineBreak) : written);
		}

		var own = leading.SkipWhile(IsWhitespace).ToArray();
		var opensWithBlankLine = own.Length > 0 && own[0].IsKind(SyntaxKind.EndOfLineTrivia);

		return argument.WithLeadingTrivia(opensWithBlankLine ? own : indentation.Concat(own));
	}

	/// <summary>
	/// Whether the token is the last on its line. Its trailing trivia runs up to and includes the line
	/// break that ends it, so a break anywhere in it, after a comment or not, is the end of the line.
	/// </summary>
	private static bool EndsLine(SyntaxToken token) =>
		token.TrailingTrivia.Any(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

	/// <summary>Whitespace within a line, which is indentation or the space between tokens, never a line break.</summary>
	private static bool IsWhitespace(SyntaxTrivia trivia) => trivia.IsKind(SyntaxKind.WhitespaceTrivia);

	/// <summary>Whitespace or a line break: trivia that is layout rather than anything the caller wrote.</summary>
	private static bool IsLayout(SyntaxTrivia trivia) =>
		trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);

	/// <summary>The trivia after the last line break in a list, which is what is on the line it ends on.</summary>
	private static IEnumerable<SyntaxTrivia> AfterLastBreak(SyntaxTriviaList trivia)
	{
		var last = -1;

		for (var index = 0; index < trivia.Count; index++)
		{
			if (trivia[index].IsKind(SyntaxKind.EndOfLineTrivia)) last = index;
		}

		return trivia.Skip(last + 1);
	}

	/// <summary>
	/// The comment ending the line a comma ends, with the whitespace in front of it, or nothing where
	/// the comma does not end its line or has no comment after it.
	/// </summary>
	private static SyntaxTriviaList LineEndComment(SyntaxToken separator)
	{
		if (!EndsLine(separator)) return default;

		var trailing = separator.TrailingTrivia;

		return [.. trailing.Take(LastWritten(trailing) + 1)];
	}

	/// <summary>A comma's trailing trivia without the comment ending its line: the position's layout alone.</summary>
	private static IEnumerable<SyntaxTrivia> Layout(SyntaxToken separator) =>
		separator.TrailingTrivia.Skip(LineEndComment(separator).Count);

	/// <summary>
	/// The argument that ends the list, with what ends the list after it: the comment that ended its
	/// line where the comma carrying that comment is gone, and the line break in front of a closing
	/// parenthesis written on a line of its own.
	/// <para>
	/// Both belong to a position rather than to an argument as Roslyn hands them over. The break before
	/// a parenthesis on its own line is the last argument's trailing trivia, so left there it follows
	/// that argument when something is appended after it, and the comma lands at column zero on the
	/// line the parenthesis was on. A comment ending the line after a comma is the comma's, so taking
	/// out the parameters after it would delete it without a word.
	/// </para>
	/// <para>
	/// A line comment runs to the end of its line, so one kept here needs a break after it or it would
	/// swallow the parenthesis. Where the list had none to give, the parenthesis goes on the next line
	/// at the call's continuation indentation -- unless something in front of it already indents it.
	/// </para>
	/// </summary>
	private static ArgumentSyntax Closing(
		ArgumentSyntax argument,
		SyntaxTriviaList comment,
		SyntaxTriviaList layout,
		ArgumentListSyntax arguments,
		SyntaxTriviaList indentation,
		SyntaxTrivia lineBreak)
	{
		var trailing = EndingLineComment(argument.GetTrailingTrivia().Concat(comment).Concat(layout), lineBreak, out var broke);
		var indented = arguments.CloseParenToken.LeadingTrivia.Any(IsWhitespace);

		return argument.WithTrailingTrivia(broke && !indented ? trailing.AddRange(indentation) : trailing);
	}

	/// <summary>
	/// The list with the line ending after its last argument taken off that argument: the comment on
	/// that line, which is the argument's and goes where it goes, and the break and whitespace after it,
	/// which are the closing parenthesis's and go to whichever argument ends up last. Nothing is taken
	/// where the last argument and the parenthesis share a line.
	/// </summary>
	private static ArgumentListSyntax WithoutClosingLine(
		ArgumentListSyntax arguments,
		out SyntaxTriviaList comment,
		out SyntaxTriviaList layout)
	{
		comment = default;
		layout = default;

		if (arguments.Arguments.Count == 0) return arguments;

		var last = arguments.Arguments[^1];
		var trailing = last.GetTrailingTrivia();

		if (!trailing.Any(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia))) return arguments;

		var end = LastWritten(trailing);

		comment = [.. trailing.Take(end + 1)];
		layout = [.. trailing.Skip(end + 1)];

		return arguments.WithArguments(arguments.Arguments.Replace(last, last.WithTrailingTrivia()));
	}

	/// <summary>
	/// The comment that ended the line after each argument as written, by position: the one after its
	/// comma, or for the last argument the one before the closing parenthesis's line break.
	/// </summary>
	private static SyntaxTriviaList[] LineEndComments(ArgumentListSyntax arguments, SyntaxTriviaList last)
	{
		var separators = arguments.Arguments.GetSeparators().ToArray();
		var comments = new SyntaxTriviaList[arguments.Arguments.Count];

		for (var index = 0; index < comments.Length; index++)
		{
			comments[index] = index < separators.Length ? LineEndComment(separators[index]) : last;
		}

		return comments;
	}

	/// <summary>The index of the last trivia that is not layout, or -1 where all of it is.</summary>
	private static int LastWritten(SyntaxTriviaList trivia)
	{
		var last = -1;

		for (var index = 0; index < trivia.Count; index++)
		{
			if (!IsLayout(trivia[index])) last = index;
		}

		return last;
	}

	/// <summary>
	/// A line break as this call site writes them, so one the rewrite adds matches the file's endings.
	/// Directives hold theirs inside their own structure. A list with none never needs one: a break is
	/// only added after a line comment or in front of a directive, and both bring a break of their own.
	/// </summary>
	private static SyntaxTrivia LineBreakIn(ArgumentListSyntax arguments)
	{
		var found = arguments.DescendantTrivia(descendIntoTrivia: true)
			.FirstOrDefault(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));

		return found.IsKind(SyntaxKind.EndOfLineTrivia) ? found : SyntaxFactory.CarriageReturnLineFeed;
	}

	/// <summary>
	/// Trailing trivia that ends its line wherever a line comment is the last thing in it, with any
	/// whitespace after the comment replaced by the break.
	/// <para>
	/// A line comment runs to the end of its line, so whatever the rewrite puts after it on that line
	/// is inside the comment: an argument appended there vanishes, and when its parameter has a default
	/// the call still compiles without it. Every token the rewrite gives a comment it carried from
	/// somewhere else goes through here, so no path can leave one followed by code.
	/// </para>
	/// </summary>
	/// <param name="trivia">The trailing trivia as composed.</param>
	/// <param name="lineBreak">The call site's own line ending, which is the break it writes.</param>
	/// <param name="broke">Whether a break was added, so the caller can indent what follows it.</param>
	private static SyntaxTriviaList EndingLineComment(
		IEnumerable<SyntaxTrivia> trivia,
		SyntaxTrivia lineBreak,
		out bool broke)
	{
		SyntaxTriviaList list = [.. trivia];
		var last = LastWritten(list);

		broke = last >= 0
			&& list[last].IsKind(SyntaxKind.SingleLineCommentTrivia)
			&& !list.Skip(last + 1).Any(item => item.IsKind(SyntaxKind.EndOfLineTrivia));

		return broke ? [.. list.Take(last + 1), lineBreak] : list;
	}

	/// <summary>
	/// The indentation for an argument a line break puts on a line of its own where no argument began
	/// one: a level inside a closing parenthesis on a line of its own, or column zero where the
	/// parenthesis does not say how deep the call is.
	/// </summary>
	private static SyntaxTriviaList InsideClosingParenthesis(ArgumentListSyntax arguments)
	{
		var own = AfterLastBreak(arguments.CloseParenToken.LeadingTrivia).TakeWhile(IsWhitespace).ToArray();

		if (own.Length == 0) return default;

		var tabbed = own.All(trivia => trivia.ToString().All(character => character == '\t'));

		return [.. own, SyntaxFactory.Whitespace(tabbed ? "\t" : "    ")];
	}

	/// <summary>
	/// Whether an argument or comma this change takes out carries a preprocessor directive. A directive
	/// sits in the leading trivia of the token after it, so taking that token out takes the directive
	/// too -- an <c>#if</c> written in front of a removed argument goes and leaves its <c>#endif</c>,
	/// which is CS1028. Which arguments a conditional block was meant to hold is not something to
	/// guess, so the site is left to a person.
	/// </summary>
	/// <param name="arguments">The list as written.</param>
	/// <param name="origins">Where each emitted argument stood in the list as written, or -1 for one that is new.</param>
	private static bool LosesDirective(ArgumentListSyntax arguments, IReadOnlyList<int> origins)
	{
		var kept = origins.Where(origin => origin >= 0).ToHashSet();

		var argumentGoes = arguments.Arguments
			.Where((_, index) => !kept.Contains(index))
			.Any(argument => argument.ContainsDirectives);

		// Commas are kept by position, so the ones past the new count are the ones that go.
		var commaGoes = arguments.Arguments.GetSeparators()
			.Skip(Math.Max(origins.Count - 1, 0))
			.Any(separator => separator.ContainsDirectives);

		return argumentGoes || commaGoes;
	}

	/// <summary>
	/// The list with any comment written between a comma and the argument after it, on the same line,
	/// moved onto that argument. It labels the argument it sits in front of, so it has to move with it
	/// rather than stay with a comma that a change gives to a different argument. The text comes out
	/// identical; only which token owns the comment changes.
	/// </summary>
	private static ArgumentListSyntax WithInlineCommentsOnTheirArgument(ArgumentListSyntax arguments)
	{
		var nodes = arguments.Arguments.ToList();
		var separators = arguments.Arguments.GetSeparators().ToList();
		var moved = false;

		for (var index = 0; index < separators.Count && index + 1 < nodes.Count; index++)
		{
			var separator = separators[index];
			var isInline = !EndsLine(separator) && separator.TrailingTrivia.Any(MemberSyntax.IsComment);

			if (!isInline) continue;

			var space = separator.TrailingTrivia.TakeWhile(IsWhitespace).ToArray();
			var comment = separator.TrailingTrivia.Skip(space.Length);

			separators[index] = separator.WithTrailingTrivia(space);
			nodes[index + 1] = nodes[index + 1].WithLeadingTrivia(comment.Concat(nodes[index + 1].GetLeadingTrivia()));
			moved = true;
		}

		return moved ? arguments.WithArguments(SyntaxFactory.SeparatedList(nodes, separators)) : arguments;
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
			// Built with no whitespace of its own: where it lands decides that, once the list it
			// lands in is known.
			wanted = [SyntaxFactory.Argument(SyntaxFactory.ParseExpression(expression))];

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
