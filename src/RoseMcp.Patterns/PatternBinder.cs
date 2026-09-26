using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoseMcp.Patterns;

/// <summary>
/// Binds a rule set's finds in one compilation, by compiling each inside a scratch method added to it.
/// <para>
/// The scratch method is what makes a pattern mean what it would mean in that project. Its names
/// resolve through the project's own references, global aliases and implicit usings, so
/// <c>Assert.Equal</c> reaches whatever <c>Assert</c> is there; and each placeholder is a parameter of
/// the method, typed by its constraint or as <c>object</c>, so the pattern is ordinary code with
/// holes that have types.
/// </para>
/// <para>
/// What is taken from the scratch compilation is the method <em>group</em>, not the overload the call
/// resolves to. An untyped placeholder makes the resolved overload the wrong question:
/// <c>Assert.Equal(object, object)</c> resolves cleanly to <c>Equal&lt;object&gt;</c>, one overload and
/// the wrong one. <c>GetMemberGroup</c> answers whether resolution succeeded or not, and the group is
/// narrowed to the overloads the pattern's shape fits -- its argument count, the parameters it names,
/// its type-argument count -- which is the set a rule covers.
/// </para>
/// </summary>
internal static class PatternBinder
{
	/// <summary>The scratch class's name: one no project declares, in the global namespace so global usings reach it.</summary>
	private const string ScratchClass = "__RoseMcpPatterns";

	/// <summary>
	/// The type an untyped placeholder is given where it is compared, declaring every comparison a find can
	/// write. As <c>object</c>, <c>$a$ &gt; $b$</c> would not compile, the call around it would have no
	/// receiver type, and the rule would be refused for a method it names correctly. A class, so that no
	/// lifted nullable operator competes with the ones it declares.
	/// </summary>
	private const string OperandClass = "__RoseMcpOperand";

	/// <summary>The comparisons a find can write, as the operator each one is in the target's operation tree.</summary>
	internal static readonly IReadOnlyDictionary<SyntaxKind, BinaryOperatorKind> Comparisons = new Dictionary<SyntaxKind, BinaryOperatorKind>
	{
		[SyntaxKind.EqualsExpression] = BinaryOperatorKind.Equals,
		[SyntaxKind.NotEqualsExpression] = BinaryOperatorKind.NotEquals,
		[SyntaxKind.LessThanExpression] = BinaryOperatorKind.LessThan,
		[SyntaxKind.LessThanOrEqualExpression] = BinaryOperatorKind.LessThanOrEqual,
		[SyntaxKind.GreaterThanExpression] = BinaryOperatorKind.GreaterThan,
		[SyntaxKind.GreaterThanOrEqualExpression] = BinaryOperatorKind.GreaterThanOrEqual,
	};

	/// <summary>
	/// Every rule of <paramref name="rules"/> bound in <paramref name="target"/>, or the reason a rule
	/// does not bind there -- a name the project does not have, most often, since not every project
	/// references what a rule set is about.
	/// </summary>
	public static BoundCatalog Bind(RuleCatalog rules, CSharpCompilation target, CancellationToken cancellationToken)
	{
		var options = target.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? Pattern.Options;
		var tree = CSharpSyntaxTree.ParseText(Scratch(rules), options, cancellationToken: cancellationToken);
		var model = target.AddSyntaxTrees(tree).GetSemanticModel(tree);

		var methods = tree.GetRoot(cancellationToken)
			.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.ToDictionary(method => method.Identifier.ValueText, StringComparer.Ordinal);

		var bound = new List<BoundRule>();
		var unbound = new Dictionary<int, string>();

		foreach (var rule in rules.Rules)
		{
			var method = methods[MethodName(rule)];
			var body = (ExpressionStatementSyntax)method.Body!.Statements[0];
			var binder = new Binder(rule, model, target, method, cancellationToken);

			try
			{
				bound.Add(new BoundRule(rule, binder.Invocation((InvocationExpressionSyntax)body.Expression)));
			}
			catch (UnboundException exception)
			{
				unbound[rule.Number] = exception.Message;
			}
		}

		Shadowing.Refuse(bound);

		return new BoundCatalog(rules, target, bound, unbound);
	}

	/// <summary>The scratch method a rule's find is compiled in.</summary>
	private static string MethodName(Rule rule) => $"Rule{rule.Number}";

	/// <summary>
	/// The scratch source: the rule set's usings, then one method per rule whose parameters are its
	/// expression placeholders and whose type parameters are its type placeholders.
	/// </summary>
	private static string Scratch(RuleCatalog rules)
	{
		var source = new StringBuilder();

		foreach (var directive in rules.Usings) source.Append("using ").Append(directive).AppendLine(";");

		source.Append("internal static class ").AppendLine(ScratchClass).AppendLine("{");
		source.Append(Operand());

		foreach (var rule in rules.Rules)
		{
			var placeholders = rule.Find.Placeholders.Values;
			var types = placeholders.Where(placeholder => placeholder.Kind == PlaceholderKind.Type).ToList();
			var compared = Compared(rule.Find.Root);

			var parameters = placeholders
				.Where(placeholder => placeholder.Kind == PlaceholderKind.Expression)
				.Select(placeholder =>
				{
					var type = placeholder.Constraint ?? (compared.Contains(placeholder.Name) ? OperandClass : "object");

					return $"{type} {PatternLexer.Prefix}{placeholder.Name}";
				});

			source.Append("\tprivate static void ").Append(MethodName(rule));

			if (types.Count > 0)
			{
				source.Append('<').AppendJoin(", ", types.Select(type => PatternLexer.Prefix + type.Name)).Append('>');
			}

			source.Append('(').AppendJoin(", ", parameters).AppendLine(")").AppendLine("\t{");
			source.Append("\t\t").Append(rule.Find.Lexed.Code).AppendLine(rule.Find.IsStatement ? string.Empty : ";");
			source.AppendLine("\t}");
		}

		return source.AppendLine("}").ToString();
	}

	/// <summary>The placeholders a find writes directly as an operand of a comparison.</summary>
	private static HashSet<string> Compared(SyntaxNode root)
	{
		var operands = root.DescendantNodes()
			.OfType<BinaryExpressionSyntax>()
			.Where(binary => Comparisons.ContainsKey(binary.Kind()))
			.SelectMany(binary => new[] { binary.Left, binary.Right });

		var names = new HashSet<string>(StringComparer.Ordinal);

		foreach (var operand in operands)
		{
			var bare = operand;
			while (bare is ParenthesizedExpressionSyntax parenthesized) bare = parenthesized.Expression;

			if (bare is IdentifierNameSyntax identifier && Pattern.IsPlaceholder(identifier.Identifier, out var name)) names.Add(name);
		}

		return names;
	}

	/// <summary>
	/// The operand class's source: each comparison against another operand and against anything at all,
	/// in both orders, so an untyped placeholder compares with a constant, a member or another placeholder.
	/// </summary>
	private static string Operand()
	{
		var source = new StringBuilder();

		source.Append("\tprivate sealed class ").AppendLine(OperandClass).AppendLine("\t{");

		foreach (var comparison in new[] { "==", "!=", "<", "<=", ">", ">=" })
		{
			foreach (var (left, right) in new[] { (OperandClass, OperandClass), (OperandClass, "object"), ("object", OperandClass) })
			{
				source.Append("\t\tpublic static bool operator ").Append(comparison)
					.Append('(').Append(left).Append(" left, ").Append(right).AppendLine(" right) => true;");
			}
		}

		source.AppendLine("\t\tpublic override bool Equals(object other) => false;");
		source.AppendLine("\t\tpublic override int GetHashCode() => 0;");

		return source.AppendLine("\t}").ToString();
	}

	/// <summary>A rule that does not bind in this compilation, carrying the reason as its message.</summary>
	private sealed class UnboundException(string reason) : Exception(reason);

	/// <summary>Binds one rule's find, node by node, against the scratch model.</summary>
	private sealed class Binder(
		Rule rule,
		SemanticModel model,
		CSharpCompilation target,
		MethodDeclarationSyntax method,
		CancellationToken cancellationToken)
	{
		/// <summary>A call, as the overloads its shape fits.</summary>
		public InvocationNode Invocation(InvocationExpressionSyntax call)
		{
			if (Rule.Invoked(call) is not { } name || call.Expression is MemberBindingExpressionSyntax)
			{
				throw Unsupported(call);
			}

			var receiverSyntax = (call.Expression as MemberAccessExpressionSyntax)?.Expression;
			var group = Group(call, name);

			if (group.Count == 0)
			{
				// A receiver that is more than a name and does not bind leaves the method nothing to be looked
				// up on. Binding it says what is wrong with it; saying the method is missing would send the
				// reader after a name that was right.
				var isComputed = receiverSyntax is not null and not (SimpleNameSyntax or MemberAccessExpressionSyntax);

				if (isComputed) Expression(receiverSyntax!);

				throw Unbound(call.Expression, $"no method named {name.Identifier.ValueText} is in scope");
			}

			var receiver = receiverSyntax is null || IsTypeOrNamespace(receiverSyntax) ? null : Expression(receiverSyntax);

			var typeArguments = name is GenericNameSyntax generic
				? generic.TypeArgumentList.Arguments.Select(TypeArgument).ToList()
				: null;

			var arguments = call.ArgumentList.Arguments;

			if (arguments.FirstOrDefault(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None)) is { } byReference)
			{
				throw Unsupported(byReference);
			}

			var nodes = arguments.Select(argument => Expression(argument.Expression)).ToList();

			var candidates = group
				.Select(member => Fit(member, receiver, arguments, nodes, typeArguments, call))
				.OfType<MethodCandidate>()
				.ToList();

			if (candidates.Count == 0)
			{
				throw new UnboundException(
					$"{rule.Find.Where} calls {name.Identifier.ValueText} with a shape none of its {group.Count} overloads "
					+ $"here takes: {Describe(call.ArgumentList)}");
			}

			return new InvocationNode(name.Identifier.ValueText, candidates);
		}

		/// <summary>
		/// The methods a call could mean, before any is chosen.
		/// <para>
		/// Asked of the member group first, which is the compiler's own answer. A type placeholder is a type
		/// parameter with no constraints, though, so a type argument list holding one breaks the constraints
		/// of every generic overload -- <c>Throws&lt;T&gt;</c> wants an exception -- and the group comes back
		/// empty. For a generic name the methods are then looked up by name in the receiver, which is the
		/// group the compiler would have given for any type argument that satisfied them.
		/// </para>
		/// </summary>
		private IReadOnlyList<IMethodSymbol> Group(InvocationExpressionSyntax call, SimpleNameSyntax name)
		{
			var group = model.GetMemberGroup(call.Expression, cancellationToken).OfType<IMethodSymbol>().ToList();

			if (group.Count > 0 || name is not GenericNameSyntax) return group;

			var receiver = (call.Expression as MemberAccessExpressionSyntax)?.Expression;

			var container = receiver is null
				? null
				: model.GetSymbolInfo(receiver, cancellationToken).Symbol as INamespaceOrTypeSymbol
					?? model.GetTypeInfo(receiver, cancellationToken).Type;

			return [.. model
				.LookupSymbols(call.SpanStart, container, name.Identifier.ValueText, includeReducedExtensionMethods: true)
				.OfType<IMethodSymbol>()];
		}

		/// <summary>Any other part of a find.</summary>
		private PatternNode Expression(ExpressionSyntax syntax)
		{
			switch (syntax)
			{
				case ParenthesizedExpressionSyntax parenthesized:
					return Expression(parenthesized.Expression);
				case IdentifierNameSyntax identifier when Pattern.IsPlaceholder(identifier.Identifier, out var name):
					return Placeholder(name, identifier);
				case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
					return new NotNode(Expression(prefix.Operand));
				case LambdaExpressionSyntax lambda:
					return Lambda(lambda);
				case InvocationExpressionSyntax call:
					return Invocation(call);
				case BinaryExpressionSyntax binary when Comparisons.TryGetValue(binary.Kind(), out var comparison):
					return new BinaryNode(comparison, Expression(binary.Left), Expression(binary.Right));
				case BinaryExpressionSyntax:
					throw Unsupported(syntax);
			}

			var constant = model.GetConstantValue(syntax, cancellationToken);

			if (constant.HasValue)
			{
				var type = model.GetTypeInfo(syntax, cancellationToken).Type;

				return new ConstantNode(constant.Value, type is null ? null : Resolve(type, syntax));
			}

			var symbol = model.GetSymbolInfo(syntax, cancellationToken).Symbol;

			if (symbol is IFieldSymbol { IsStatic: true } or IPropertySymbol { IsStatic: true })
			{
				return new MemberNode(Resolve(symbol.OriginalDefinition, syntax));
			}

			if (symbol is null && FirstError(syntax) is not null) throw Unbound(syntax, "it does not compile here");

			throw Unsupported(syntax);
		}

		/// <summary>An expression placeholder, with its constraint resolved in the target.</summary>
		private PlaceholderNode Placeholder(string name, SyntaxNode where)
		{
			if (rule.Find.Placeholders[name].Constraint is not { } written) return new PlaceholderNode(name, null);

			var parameter = method.ParameterList.Parameters.First(candidate => candidate.Identifier.ValueText == PatternLexer.Prefix + name);
			var type = model.GetDeclaredSymbol(parameter, cancellationToken)?.Type;

			if (type is null or { TypeKind: TypeKind.Error })
			{
				throw new UnboundException($"{rule.Find.Where} constrains ${name}$ to '{written}', which is not a type here");
			}

			return new PlaceholderNode(name, Resolve(type, where));
		}

		/// <summary>A lambda, which a find can only write as <c>$x$ =&gt; $body$</c>.</summary>
		private LambdaNode Lambda(LambdaExpressionSyntax lambda)
		{
			var parameter = lambda switch
			{
				SimpleLambdaExpressionSyntax simple => simple.Parameter,
				ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0],
				_ => null,
			};

			var isPlain = parameter is { Type: null, Modifiers.Count: 0 } && !lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);

			if (!isPlain
				|| !Pattern.IsPlaceholder(parameter!.Identifier, out var parameterName)
				|| lambda.Body is not IdentifierNameSyntax body
				|| !Pattern.IsPlaceholder(body.Identifier, out var bodyName)
				|| rule.Find.Placeholders[bodyName].Constraint is not null)
			{
				throw new PatternException(
					$"{rule.Find.Where} writes the lambda `{Written(lambda)}`. A lambda in a find is matched as "
					+ "`$x$ => $body$`: one parameter and a body, both placeholders, which captures any lambda of one "
					+ "parameter that is not async.");
			}

			return new LambdaNode(parameterName, bodyName);
		}

		/// <summary>A type argument: a placeholder, or a type the target has to write exactly.</summary>
		private TypeArgumentNode TypeArgument(TypeSyntax syntax)
		{
			if (syntax is IdentifierNameSyntax identifier && Pattern.IsPlaceholder(identifier.Identifier, out var name))
			{
				return new TypePlaceholderNode(name);
			}

			var type = model.GetTypeInfo(syntax, cancellationToken).Type;

			if (type is null or { TypeKind: TypeKind.Error }) throw Unbound(syntax, "it is not a type here");

			return new ConcreteTypeNode(Resolve(type, syntax));
		}

		/// <summary>
		/// The overload <paramref name="member"/> as the pattern's shape maps onto it, or null when the
		/// shape does not fit it.
		/// </summary>
		private MethodCandidate? Fit(
			IMethodSymbol member,
			PatternNode? receiver,
			SeparatedSyntaxList<ArgumentSyntax> arguments,
			IReadOnlyList<PatternNode> nodes,
			IReadOnlyList<TypeArgumentNode>? typeArguments,
			SyntaxNode where)
		{
			var isReduced = member.ReducedFrom is not null;
			var definition = (member.ReducedFrom ?? member).OriginalDefinition;

			if (typeArguments is not null && definition.Arity != typeArguments.Count) return null;

			var map = new Dictionary<int, PatternNode>();
			PatternNode? instance = null;
			var shift = 0;

			if (isReduced)
			{
				// An extension method called on its receiver. The receiver is its first parameter's
				// argument, which is how the target's operation tree presents it too.
				if (receiver is null) return null;

				map[0] = receiver;
				shift = 1;
			}
			else if (receiver is not null)
			{
				if (definition.IsStatic) return null;

				instance = receiver;
			}
			else if (!definition.IsStatic)
			{
				return null;
			}

			for (var index = 0; index < arguments.Count; index++)
			{
				var ordinal = arguments[index].NameColon is { } named
					? definition.Parameters.FirstOrDefault(parameter => parameter.Name == named.Name.Identifier.ValueText)?.Ordinal
					: index + shift;

				if (ordinal is not { } slot || slot >= definition.Parameters.Length || !map.TryAdd(slot, nodes[index])) return null;
			}

			var leavesARequiredParameter = definition.Parameters.Any(
				parameter => !map.ContainsKey(parameter.Ordinal) && !parameter.IsOptional && !parameter.IsParams);

			if (leavesARequiredParameter) return null;

			return new MethodCandidate(Resolve(definition, where), instance, map, typeArguments);
		}

		/// <summary>Whether a receiver names a type or a namespace, which makes the call a static one.</summary>
		private bool IsTypeOrNamespace(ExpressionSyntax receiver) =>
			model.GetSymbolInfo(receiver, cancellationToken).Symbol is INamespaceOrTypeSymbol;

		/// <summary>
		/// The target compilation's own symbol for one the scratch compilation resolved, found by its
		/// documentation-comment id, which names a symbol the same way in every compilation that has it.
		/// </summary>
		private T Resolve<T>(T symbol, SyntaxNode where)
			where T : class, ISymbol
		{
			ISymbol? resolved;

			if (symbol is ITypeSymbol type)
			{
				resolved = DocumentationCommentId.GetFirstSymbolForReferenceId(DocumentationCommentId.CreateReferenceId(type), target);
			}
			else
			{
				var assembly = symbol.ContainingAssembly?.Name;

				resolved = symbol.GetDocumentationCommentId() is { } id
					? DocumentationCommentId.GetSymbolsForDeclarationId(id, target)
						.FirstOrDefault(candidate => candidate.ContainingAssembly?.Name == assembly)
					: null;
			}

			return resolved as T ?? throw Unbound(where, $"'{symbol}' cannot be found outside the pattern");
		}

		/// <summary>The first error the scratch compilation reports inside <paramref name="syntax"/>.</summary>
		private Diagnostic? FirstError(SyntaxNode syntax) => model
			.GetDiagnostics(syntax.Span, cancellationToken)
			.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

		/// <summary>A rule that does not bind here, saying what in it did not and, where there is one, the compiler's reason.</summary>
		private UnboundException Unbound(SyntaxNode syntax, string what)
		{
			var error = FirstError(syntax);
			var reason = error is null ? what : $"{what}: {error.Id}, {error.GetMessage()}";

			return new UnboundException($"{rule.Find.Where} does not bind at `{Written(syntax)}`, because {reason}");
		}

		/// <summary>A part of a find that is not one of the constructs a find is built from.</summary>
		private PatternException Unsupported(SyntaxNode syntax) => new(
			$"{rule.Find.Where} uses `{Written(syntax)}`, which a find cannot match. A find is built from calls, "
			+ "placeholders, constants, static fields and properties, `!`, the comparisons == != < <= > >=, and lambdas "
			+ "written `$x$ => $body$`. " + PatternException.Grammar);

		/// <summary>A node of the scratch source as the caller wrote it, with each placeholder back in dollar signs.</summary>
		private string Written(SyntaxNode syntax)
		{
			var text = syntax.ToString();

			foreach (var name in rule.Find.Placeholders.Keys.OrderByDescending(key => key.Length))
			{
				text = text.Replace(PatternLexer.Prefix + name, $"${name}$", StringComparison.Ordinal);
			}

			return text;
		}

		/// <summary>An argument list as a message describes its shape.</summary>
		private string Describe(ArgumentListSyntax arguments) => $"`{Written(arguments)}`";
	}
}
