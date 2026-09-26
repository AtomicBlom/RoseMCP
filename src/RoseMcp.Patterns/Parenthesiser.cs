using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>
/// Decides whether a captured expression needs parentheses where a replacement puts it.
/// <para>
/// A capture moves from wherever it was written -- usually an argument, where nothing binds tighter
/// than the comma -- to wherever the template puts it, and the commonest place is a receiver:
/// <c>Assert.Equal(1, a?.B)</c> becomes <c>$a$.ShouldBe($e$)</c>. Written without parentheses that
/// is <c>a?.B.ShouldBe(1)</c>, which compiles and skips the assertion whenever <c>a</c> is null. So
/// the rule is a list of the forms that can never need parentheses, and parentheses for everything
/// else: the worst it does is add a pair nobody needed, and it can never drop one that was.
/// </para>
/// <para>
/// Roslyn's simplifier can remove redundant parentheses, and is not used: it runs every reducer it has
/// across the span it is given, which would include the caller's own text inside the capture, and it
/// needs Workspaces, which this assembly does without.
/// </para>
/// </summary>
internal static class Parenthesiser
{
	/// <summary>
	/// <paramref name="capture"/> as it should be written in place of <paramref name="placeholder"/>:
	/// parenthesised when the position binds tighter than the capture and the capture is not one of the
	/// forms that is safe anywhere.
	/// </summary>
	public static ExpressionSyntax Fit(ExpressionSyntax capture, SyntaxNode placeholder)
	{
		if (IsWholeExpressionPosition(placeholder) || IsSafeAnywhere(capture)) return capture;

		return SyntaxFactory.ParenthesizedExpression(capture);
	}

	/// <summary>
	/// Whether a placeholder stands where a whole expression goes, so nothing around it binds to part of
	/// what replaces it: an argument, an initialiser, a statement, a return, a lambda body, a loop's
	/// collection, the inside of parentheses.
	/// </summary>
	private static bool IsWholeExpressionPosition(SyntaxNode placeholder) => placeholder.Parent switch
	{
		ArgumentSyntax or AttributeArgumentSyntax => true,
		EqualsValueClauseSyntax or ArrowExpressionClauseSyntax => true,
		ExpressionStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax or YieldStatementSyntax => true,
		ParenthesizedExpressionSyntax or InterpolationSyntax => true,
		InitializerExpressionSyntax or ExpressionElementSyntax => true,
		ForEachStatementSyntax loop => loop.Expression == placeholder,
		LambdaExpressionSyntax lambda => lambda.Body == placeholder,
		IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or LockStatementSyntax or UsingStatementSyntax => true,
		SwitchStatementSyntax or SwitchExpressionSyntax => true,
		_ => false,
	};

	/// <summary>
	/// Whether an expression reads the same wherever it is put: a name, a member access, a call, an
	/// element access, a literal, and the other primary forms. A conditional access is deliberately not
	/// one of them, since a member written after it joins the null check.
	/// </summary>
	private static bool IsSafeAnywhere(ExpressionSyntax capture) => capture switch
	{
		IdentifierNameSyntax or GenericNameSyntax or AliasQualifiedNameSyntax or PredefinedTypeSyntax => true,
		MemberAccessExpressionSyntax access => access.IsKind(SyntaxKind.SimpleMemberAccessExpression),
		InvocationExpressionSyntax or ElementAccessExpressionSyntax or ParenthesizedExpressionSyntax => true,
		LiteralExpressionSyntax or InterpolatedStringExpressionSyntax => true,
		TypeOfExpressionSyntax or DefaultExpressionSyntax or SizeOfExpressionSyntax or CheckedExpressionSyntax => true,
		ThisExpressionSyntax or BaseExpressionSyntax => true,
		ObjectCreationExpressionSyntax or AnonymousObjectCreationExpressionSyntax => true,
		PostfixUnaryExpressionSyntax postfix => postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression) && IsSafeAnywhere(postfix.Operand),
		_ => false,
	};
}
