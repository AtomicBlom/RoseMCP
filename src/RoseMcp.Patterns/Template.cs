using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>
/// Writes a rule's replace for one site: the template with each placeholder filled by what the find
/// captured there.
/// <para>
/// A capture is the caller's own syntax moved into place, never regenerated from its text, so what it
/// holds -- comments, a verbatim string, the spelling of a name -- arrives exactly as written. Only the
/// whitespace at its outer edges is dropped, since it described where the capture used to sit; the
/// formatter lays it out where it sits now. Two things survive that: a comment at an edge, because a line
/// comment without the line break after it would swallow the code that follows, and the line break before
/// an argument the caller put on a line of its own, because a call is not the formatter's to wrap.
/// </para>
/// </summary>
internal static class Template
{
	/// <summary>
	/// <paramref name="rule"/>'s replace filled with <paramref name="captures"/>, or null with the reason
	/// the site cannot take it.
	/// </summary>
	/// <param name="rule">The rule that won the site.</param>
	/// <param name="captures">What each placeholder captured, after any rewrite inside it.</param>
	/// <param name="site">The site in the original tree, whose position decides whether the replacement needs parentheses.</param>
	/// <param name="refusal">Why the site cannot take the replacement, when it cannot.</param>
	public static SyntaxNode? Instantiate(Rule rule, IReadOnlyDictionary<string, CapturedSyntax> captures, SyntaxNode site, out string? refusal)
	{
		var substitution = new Substitution(rule, captures);
		var filled = substitution.Visit(rule.Replace.Root);

		refusal = substitution.Refusal;

		if (refusal is not null || filled is null) return null;

		filled = WithoutSpaceBeforeBreaks(filled);

		return filled is ExpressionSyntax expression ? Parenthesiser.Fit(expression, site) : filled;
	}

	/// <summary>
	/// <paramref name="node"/> without the template's spacing where a capture now begins a line: the space
	/// after a comma in <c>, $m$</c> would otherwise end the line before an argument the caller put on a
	/// line of its own.
	/// </summary>
	private static SyntaxNode WithoutSpaceBeforeBreaks(SyntaxNode node)
	{
		var spaced = node.DescendantTokens()
			.Where(token => token.TrailingTrivia.Count > 0
				&& token.TrailingTrivia.All(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia))
				&& token.GetNextToken().LeadingTrivia.FirstOrDefault().IsKind(SyntaxKind.EndOfLineTrivia))
			.ToList();

		return spaced.Count == 0 ? node : node.ReplaceTokens(spaced, (_, rewritten) => rewritten.WithTrailingTrivia());
	}

	/// <summary>A node with the whitespace at its edges dropped, unless an edge holds a comment.</summary>
	internal static T Clean<T>(T node)
		where T : SyntaxNode => node
		.WithLeadingTrivia(Edge(node.GetLeadingTrivia()))
		.WithTrailingTrivia(Edge(node.GetTrailingTrivia()));

	/// <summary>An edge's trivia: none when it is only layout, all of it when it holds a comment.</summary>
	private static SyntaxTriviaList Edge(SyntaxTriviaList trivia)
	{
		var isLayout = trivia.All(item => item.IsKind(SyntaxKind.WhitespaceTrivia) || item.IsKind(SyntaxKind.EndOfLineTrivia));

		return isLayout ? default : trivia;
	}

	/// <summary>Fills a template's placeholders.</summary>
	private sealed class Substitution(Rule rule, IReadOnlyDictionary<string, CapturedSyntax> captures) : CSharpSyntaxRewriter
	{
		/// <summary>Why this site cannot take the replacement, once something says it cannot.</summary>
		public string? Refusal { get; private set; }

		public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
		{
			if (!Pattern.IsPlaceholder(node.Identifier, out var name)) return base.VisitIdentifierName(node);

			var capture = captures[name];

			switch (rule.Find.Placeholders[name].Kind)
			{
				case PlaceholderKind.Identifier:
					return SyntaxFactory.IdentifierName(capture.Token.Text).WithTriviaFrom(node);
				case PlaceholderKind.Type:
					return Around(Clean(capture.Node!), node);
			}

			if (capture.Node is ExpressionSyntax expression)
			{
				var placed = Around(Parenthesiser.Fit(Clean(expression), node), node);
				var keepsItsLine = capture.LineBreak.Count > 0 && node.Parent is ArgumentSyntax;

				return keepsItsLine ? placed.WithLeadingTrivia(capture.LineBreak.AddRange(placed.GetLeadingTrivia())) : placed;
			}

			Refusal ??= $"the lambda it matched has a block body, and rule {rule.Number}'s replace writes ${name}$ where an "
				+ "expression goes; a block can only be written where a statement goes";

			return node;
		}

		/// <summary>
		/// A capture with the template's own spacing around it, kept outside whatever the capture carries at
		/// its edges -- a comment written beside it goes with it rather than being replaced.
		/// </summary>
		private static SyntaxNode Around(SyntaxNode capture, SyntaxNode placeholder) => capture
			.WithLeadingTrivia(placeholder.GetLeadingTrivia().AddRange(capture.GetLeadingTrivia()))
			.WithTrailingTrivia(capture.GetTrailingTrivia().AddRange(placeholder.GetTrailingTrivia()));

		public override SyntaxToken VisitToken(SyntaxToken token)
		{
			var isDeclaredName = Pattern.IsPlaceholder(token, out var name) && token.Parent is not IdentifierNameSyntax;

			if (!isDeclaredName) return base.VisitToken(token);

			return SyntaxFactory.Identifier(captures[name].Token.Text).WithTriviaFrom(token);
		}

		public override SyntaxNode? VisitBlock(BlockSyntax node)
		{
			// A block holding nothing but the body is the body's own block, braces and comments included,
			// rather than a block around another block.
			if (node.Statements is [ExpressionStatementSyntax only] && Body(only) is { Node: BlockSyntax block })
			{
				return Statement(block) is { } body ? body.WithTriviaFrom(node) : node;
			}

			return base.VisitBlock(node);
		}

		public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
		{
			if (Body(node) is not { } capture) return base.VisitExpressionStatement(node);

			return capture.Node switch
			{
				BlockSyntax block => Statement(block) is { } body ? body.WithTriviaFrom(node) : node,
				ExpressionSyntax expression when IsStatementExpression(expression) =>
					SyntaxFactory.ExpressionStatement(Clean(expression)).WithTriviaFrom(node),
				_ => Refuse(node, "the lambda body it matched is an expression that cannot stand as a statement"),
			};
		}

		/// <summary>The capture a statement consisting of one expression placeholder stands for, if it is one.</summary>
		private CapturedSyntax? Body(ExpressionStatementSyntax statement)
		{
			var isPlaceholder = statement.Expression is IdentifierNameSyntax identifier
				&& Pattern.IsPlaceholder(identifier.Identifier, out _);

			if (!isPlaceholder) return null;

			Pattern.IsPlaceholder(((IdentifierNameSyntax)statement.Expression).Identifier, out var name);

			return rule.Find.Placeholders[name].Kind == PlaceholderKind.Expression ? captures[name] : null;
		}

		/// <summary>
		/// A captured block written as a statement, or a refusal when it returns: a return that left a
		/// lambda would leave the whole method once the body is a loop's.
		/// </summary>
		private StatementSyntax? Statement(BlockSyntax block)
		{
			var returns = block.DescendantNodes(descendIntoChildren: node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
				.OfType<ReturnStatementSyntax>()
				.Any();

			if (!returns) return Clean(block);

			Refusal ??= "the lambda body it matched returns, and written as a statement that return would leave the method";

			return null;
		}

		/// <summary>Records a refusal and leaves the node as it was.</summary>
		private SyntaxNode Refuse(SyntaxNode node, string reason)
		{
			Refusal ??= reason;

			return node;
		}

		/// <summary>Whether an expression is one C# lets stand as a statement on its own.</summary>
		private static bool IsStatementExpression(ExpressionSyntax expression) => expression switch
		{
			InvocationExpressionSyntax or AssignmentExpressionSyntax or AwaitExpressionSyntax => true,
			ObjectCreationExpressionSyntax or ConditionalAccessExpressionSyntax => true,
			PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax =>
				expression.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
					or SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression,
			_ => false,
		};
	}
}

/// <summary>
/// What one placeholder captured, as the replacement will use it: the captured node after any rewrite
/// inside it, or the captured identifier.
/// </summary>
/// <param name="Node">The expression, type or lambda body.</param>
/// <param name="Token">The identifier, for an identifier placeholder.</param>
/// <param name="LineBreak">
/// The line break and indentation the capture began its line with, when the caller put it on a line of
/// its own inside the site; empty when it shared a line.
/// </param>
internal readonly record struct CapturedSyntax(SyntaxNode? Node, SyntaxToken Token, SyntaxTriviaList LineBreak = default);
