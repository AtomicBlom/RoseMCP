using Microsoft.CodeAnalysis;

namespace RoseMcp.Patterns;

/// <summary>
/// A find, bound in one compilation: each part of it resolved to the symbols it names there, and each
/// placeholder to the type its capture has to convert to.
/// <para>
/// The symbols are the target compilation's own, not the scratch compilation's the pattern was bound
/// in, so a match is a comparison of two symbols from the same compilation and never of two spellings.
/// </para>
/// </summary>
internal abstract record PatternNode;

/// <summary>A placeholder that captures an expression, or a lambda's body.</summary>
/// <param name="Name">The placeholder's name.</param>
/// <param name="Constraint">The type the capture has to convert to, or null for any expression.</param>
internal sealed record PlaceholderNode(string Name, ITypeSymbol? Constraint) : PatternNode;

/// <summary>A constant the target has to have the same value and type as: <c>true</c>, <c>StringComparison.Ordinal</c>.</summary>
/// <param name="Value">The value, which is null for the null literal.</param>
/// <param name="Type">Its type, or null for the null literal, which has none.</param>
internal sealed record ConstantNode(object? Value, ITypeSymbol? Type) : PatternNode;

/// <summary>A static field or property that is not a constant: <c>string.Empty</c>.</summary>
/// <param name="Member">The member the target has to reference.</param>
internal sealed record MemberNode(ISymbol Member) : PatternNode;

/// <summary>A logical not of something else the pattern says.</summary>
/// <param name="Operand">What is negated.</param>
internal sealed record NotNode(PatternNode Operand) : PatternNode;

/// <summary>A lambda with one parameter, both captured: <c>$x$ => $body$</c>.</summary>
/// <param name="Parameter">The placeholder that captures the parameter's identifier.</param>
/// <param name="Body">The placeholder that captures the body, an expression or a block.</param>
internal sealed record LambdaNode(string Parameter, string Body) : PatternNode;

/// <summary>
/// A call, as every overload the pattern's shape fits. A target matches when the method it calls is
/// one of them and its arguments match that overload's.
/// </summary>
/// <param name="Name">The method's name, for messages.</param>
/// <param name="Candidates">The overloads the pattern's shape fits, each with its arguments mapped to parameters.</param>
internal sealed record InvocationNode(string Name, IReadOnlyList<MethodCandidate> Candidates) : PatternNode;

/// <summary>
/// One overload a call pattern fits, with the pattern's arguments mapped to its parameters.
/// <para>
/// Mapped by parameter rather than by position, so a target that names its arguments, reorders them,
/// or calls an extension method in either form maps onto the same ordinals the pattern does.
/// </para>
/// </summary>
/// <param name="Method">The overload, as its original definition.</param>
/// <param name="Instance">What the pattern says the instance is, for an instance method; null otherwise.</param>
/// <param name="Arguments">
/// What the pattern says each parameter's argument is, by ordinal. A parameter with no entry must be
/// left to its default at the target.
/// </param>
/// <param name="TypeArguments">
/// The type arguments the pattern writes, or null when it writes none -- in which case the target
/// must write none either, since a type argument the caller chose can change what is compared.
/// </param>
internal sealed record MethodCandidate(
	IMethodSymbol Method,
	PatternNode? Instance,
	IReadOnlyDictionary<int, PatternNode> Arguments,
	IReadOnlyList<TypeArgumentNode>? TypeArguments);

/// <summary>One type argument a call pattern writes.</summary>
internal abstract record TypeArgumentNode;

/// <summary>A type argument captured by a placeholder.</summary>
/// <param name="Name">The placeholder's name.</param>
internal sealed record TypePlaceholderNode(string Name) : TypeArgumentNode;

/// <summary>A type argument the target has to write as exactly this type.</summary>
/// <param name="Type">The type.</param>
internal sealed record ConcreteTypeNode(ITypeSymbol Type) : TypeArgumentNode;
