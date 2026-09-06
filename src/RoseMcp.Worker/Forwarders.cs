using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Recognises a call site whose whole enclosing member is a forwarder: a method that does nothing
/// but call this one, passing its own parameters straight through.
/// <para>
/// Worth telling apart because a forwarder is where a signature change goes quietly wrong. A new
/// parameter with a default breaks nothing, so the forwarder compiles, goes on passing the default,
/// and every caller of <em>it</em> now silently gets behaviour the change was supposed to alter.
/// The broker-to-worker and broker-to-host splits make a five-deep chain of these the most common
/// shape of parameter change in this repository, and the tool changes one layer of it.
/// </para>
/// <para>
/// Saying which unchanged call sites are forwarders is the cheap half of the answer. Following them
/// automatically is not attempted: what name and what default the new parameter takes at each layer
/// is a decision, and one this can see no evidence for.
/// </para>
/// </summary>
public static class Forwarders
{
	/// <summary>
	/// The forwarding member a call site is the whole body of, or null where it is an ordinary call.
	/// </summary>
	/// <param name="node">The invocation, or a node inside it.</param>
	public static MemberDeclarationSyntax? Around(SyntaxNode node)
	{
		var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
		if (invocation is null) return null;

		var member = invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>();
		if (member is not BaseMethodDeclarationSyntax method) return null;

		// The body has to be this call and nothing else. An expression body is one by construction; a
		// block qualifies where its single statement is the call or a return of it.
		var only = Only(method);
		if (only is null || only != invocation) return null;

		return Passes(method, invocation) ? method : null;
	}

	/// <summary>The single expression a member's body consists of, or null where it does more.</summary>
	private static ExpressionSyntax? Only(BaseMethodDeclarationSyntax method)
	{
		if (method.ExpressionBody is { } arrow) return arrow.Expression;
		if (method.Body is not { Statements: [var statement] }) return null;

		return statement switch
		{
			ReturnStatementSyntax { Expression: { } returned } => returned,
			ExpressionStatementSyntax expression => expression.Expression,
			_ => null,
		};
	}

	/// <summary>
	/// Whether every argument is one of the member's own parameters, passed by name and nothing else.
	/// <para>
	/// Bare identifiers only. A forwarder that transforms an argument on the way through is a method
	/// that happens to have one call in it, and calling that a forwarder would tell a caller the
	/// layer is mechanical when it is not.
	/// </para>
	/// </summary>
	private static bool Passes(BaseMethodDeclarationSyntax method, InvocationExpressionSyntax invocation)
	{
		var parameters = method.ParameterList.Parameters
			.Select(parameter => parameter.Identifier.Text)
			.ToHashSet(StringComparer.Ordinal);

		if (parameters.Count == 0) return false;

		var arguments = invocation.ArgumentList.Arguments;
		if (arguments.Count == 0) return false;

		return arguments.All(argument =>
			argument.Expression is IdentifierNameSyntax name && parameters.Contains(name.Identifier.Text));
	}
}
