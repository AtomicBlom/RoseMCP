using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoseMcp.Patterns;

/// <summary>
/// Finds every site in one tree that a bound rule set matches, and every call into the same types that
/// none does.
/// <para>
/// A site is compared on its operation tree, not its text. The operation tree already says which
/// parameter each argument is for, whatever order or names they were written in; it presents an
/// extension method's receiver as its first argument whichever form the call was written in; and it
/// sees through the implicit conversions that make <c>Assert.Equal(1, count)</c> and
/// <c>Assert.Equal(1L, count)</c> different calls. The syntax is still what a placeholder captures,
/// because the capture is going to be written back, and it has to be written back as the caller wrote it.
/// </para>
/// </summary>
internal static class SiteScanner
{
	/// <summary>Scans the tree <paramref name="model"/> is a model of.</summary>
	public static ScanResult Scan(BoundCatalog rules, SemanticModel model, CancellationToken cancellationToken)
	{
		var byName = rules.Bound
			.GroupBy(rule => rule.Rule.RootName, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

		var owners = new HashSet<INamedTypeSymbol>(
			rules.Bound.SelectMany(rule => rule.Methods).Select(method => method.ContainingType.OriginalDefinition),
			SymbolEqualityComparer.Default);

		var sites = new List<PatternSite>();
		var unmatched = new List<UnmatchedCall>();

		foreach (var call in model.SyntaxTree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (Site(call, byName, model, cancellationToken) is { } site)
			{
				sites.Add(site);

				continue;
			}

			if (Missed(call, owners, model, cancellationToken) is { } missed) unmatched.Add(missed);
		}

		return new ScanResult(sites, unmatched);
	}

	/// <summary>
	/// The site <paramref name="call"/> is, when a rule matches it. The name is compared first, because a
	/// method's name cannot be aliased, so a call whose name no rule's find calls is one no rule can match
	/// and is not worth binding.
	/// </summary>
	private static PatternSite? Site(
		InvocationExpressionSyntax call,
		IReadOnlyDictionary<string, List<BoundRule>> byName,
		SemanticModel model,
		CancellationToken cancellationToken)
	{
		if (Rule.Invoked(call) is not { } name || !byName.TryGetValue(name.Identifier.ValueText, out var rules)) return null;

		var statement = call.Parent is ExpressionStatementSyntax parent && parent.Expression == call ? parent : null;

		BoundRule? winner = null;
		IReadOnlyDictionary<string, Capture>? captures = null;
		var alsoMatched = new List<int>();

		foreach (var rule in rules)
		{
			if (rule.Rule.Find.IsStatement && statement is null) continue;

			var matcher = new Matcher(model, cancellationToken);

			if (!matcher.Invocation(rule.Root, call, null)) continue;

			if (winner is null)
			{
				winner = rule;
				captures = matcher.Captures;
			}
			else
			{
				alsoMatched.Add(rule.Rule.Number);
			}
		}

		if (winner is null) return null;

		var node = winner.Rule.Find.IsStatement ? (SyntaxNode)statement! : call;

		return new PatternSite(node, winner.Rule, captures!, alsoMatched);
	}

	/// <summary>The unmatched call <paramref name="call"/> is, when it calls into one of the rules' types.</summary>
	private static UnmatchedCall? Missed(
		InvocationExpressionSyntax call,
		HashSet<INamedTypeSymbol> owners,
		SemanticModel model,
		CancellationToken cancellationToken)
	{
		if (owners.Count == 0) return null;

		var info = model.GetSymbolInfo(call, cancellationToken);

		if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol method) return null;

		var definition = (method.ReducedFrom ?? method).OriginalDefinition;

		return owners.Contains(definition.ContainingType.OriginalDefinition)
			? new UnmatchedCall(call, definition, info.Symbol is not null)
			: null;
	}

	/// <summary>
	/// An expression with the parentheses around it taken off, which is what a structure is compared
	/// on. A placeholder still captures the parentheses, since they are the caller's.
	/// </summary>
	private static ExpressionSyntax Bare(ExpressionSyntax syntax)
	{
		while (syntax is ParenthesizedExpressionSyntax parenthesized) syntax = parenthesized.Expression;

		return syntax;
	}

	/// <summary>An operation with the implicit conversions around it taken off.</summary>
	private static IOperation? Unwrapped(IOperation? operation)
	{
		while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;

		return operation;
	}

	/// <summary>Matches one find against one site, collecting what its placeholders capture.</summary>
	private sealed class Matcher(SemanticModel model, CancellationToken cancellationToken)
	{
		private readonly Dictionary<string, Capture> _captures = new(StringComparer.Ordinal);

		/// <summary>What the placeholders captured, once a match has succeeded.</summary>
		public IReadOnlyDictionary<string, Capture> Captures => _captures;

		/// <summary>
		/// Whether <paramref name="syntax"/> is a call to one of <paramref name="node"/>'s overloads, with
		/// arguments that match that overload's.
		/// </summary>
		/// <param name="node">The call the find makes.</param>
		/// <param name="syntax">The expression at the site.</param>
		/// <param name="operation">Its operation, when the caller has it; asked of the model otherwise.</param>
		public bool Invocation(InvocationNode node, ExpressionSyntax syntax, IOperation? operation)
		{
			if (Bare(syntax) is not InvocationExpressionSyntax call) return false;

			if ((Unwrapped(operation) ?? model.GetOperation(call, cancellationToken)) is not IInvocationOperation invocation)
			{
				return false;
			}

			var method = invocation.TargetMethod;
			var definition = (method.ReducedFrom ?? method).OriginalDefinition;
			var candidate = node.Candidates.FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Method, definition));

			return candidate is not null
				&& TypeArguments(candidate, call, method)
				&& Instance(candidate, call, invocation)
				&& Arguments(candidate, invocation, definition);
		}

		/// <summary>
		/// Whether the type arguments the site writes are the ones the find writes. A find that writes none
		/// matches only a site that writes none: a type argument the caller chose can change what a call
		/// compares, so dropping it in a rewrite is not safe to do silently.
		/// </summary>
		private bool TypeArguments(MethodCandidate candidate, InvocationExpressionSyntax call, IMethodSymbol method)
		{
			var written = Rule.Invoked(call) as GenericNameSyntax;

			if (candidate.TypeArguments is not { } expected) return written is null;

			if (written is null || written.TypeArgumentList.Arguments.Count != expected.Count) return false;

			for (var index = 0; index < expected.Count; index++)
			{
				switch (expected[index])
				{
					case TypePlaceholderNode placeholder:
						_captures[placeholder.Name] = new Capture(
							placeholder.Name,
							PlaceholderKind.Type,
							written.TypeArgumentList.Arguments[index],
							default);

						break;
					case ConcreteTypeNode concrete when !SymbolEqualityComparer.Default.Equals(method.TypeArguments[index], concrete.Type):
						return false;
				}
			}

			return true;
		}

		/// <summary>Whether the instance a site calls on matches the one the find names, for an instance method.</summary>
		private bool Instance(MethodCandidate candidate, InvocationExpressionSyntax call, IInvocationOperation invocation)
		{
			if (candidate.Instance is null) return true;

			var receiver = (call.Expression as MemberAccessExpressionSyntax)?.Expression;

			return receiver is not null
				&& invocation.Instance is { IsImplicit: false } instance
				&& Expression(candidate.Instance, receiver, instance);
		}

		/// <summary>
		/// Whether every parameter's argument at the site matches what the find says of it: its pattern
		/// where the find gives one, and its default where the find leaves it out.
		/// </summary>
		private bool Arguments(MethodCandidate candidate, IInvocationOperation invocation, IMethodSymbol definition)
		{
			var byOrdinal = new Dictionary<int, IArgumentOperation>();

			foreach (var argument in invocation.Arguments)
			{
				if (argument.Parameter is null) return false;

				byOrdinal[argument.Parameter.Ordinal] = argument;
			}

			foreach (var parameter in definition.Parameters)
			{
				var argument = byOrdinal.GetValueOrDefault(parameter.Ordinal);

				if (candidate.Arguments.TryGetValue(parameter.Ordinal, out var expected))
				{
					if (argument is not { ArgumentKind: ArgumentKind.Explicit } || Written(argument) is not { } written) return false;
					if (!Expression(expected, written, argument.Value)) return false;
				}
				else if (!IsDefault(argument, parameter))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Whether a parameter the find leaves out is left to its default at the site. An argument that
		/// spells the default out, as <c>ignoreCase: false</c>, counts; one that passes anything else does
		/// not, which is what keeps a rule for the plain call from swallowing one that asks for more.
		/// </summary>
		private static bool IsDefault(IArgumentOperation? argument, IParameterSymbol parameter) => argument switch
		{
			null => parameter.IsOptional || parameter.IsParams,
			{ ArgumentKind: ArgumentKind.DefaultValue } => true,
			{ ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation array } =>
				(array.Initializer?.ElementValues.Length ?? 0) == 0,
			{ ArgumentKind: ArgumentKind.Explicit } => parameter.HasExplicitDefaultValue
				&& Unwrapped(argument.Value) is { ConstantValue.HasValue: true } value
				&& Equals(value.ConstantValue.Value, parameter.ExplicitDefaultValue),
			_ => false,
		};

		/// <summary>
		/// The expression an argument is written as: inside its argument syntax, or the receiver itself for
		/// an extension method called on one. Null for a <c>ref</c> or <c>out</c> argument, which no find matches.
		/// </summary>
		private static ExpressionSyntax? Written(IArgumentOperation argument) => argument.Syntax switch
		{
			ArgumentSyntax written when written.RefKindKeyword.IsKind(SyntaxKind.None) => written.Expression,
			ArgumentSyntax => null,
			ExpressionSyntax receiver => receiver,
			_ => null,
		};

		/// <summary>Whether one part of a find matches the expression at the site.</summary>
		private bool Expression(PatternNode node, ExpressionSyntax syntax, IOperation? operation)
		{
			switch (node)
			{
				case PlaceholderNode placeholder:
					return Placeholder(placeholder, syntax);
				case InvocationNode call:
					return Invocation(call, syntax, operation);
				case NotNode not:
					return Not(not, syntax, operation);
				case LambdaNode lambda:
					return Lambda(lambda, syntax);
				case ConstantNode constant:
					var value = Unwrapped(operation) ?? model.GetOperation(Bare(syntax), cancellationToken);

					return value is { ConstantValue.HasValue: true }
						&& Equals(value.ConstantValue.Value, constant.Value)
						&& SymbolEqualityComparer.Default.Equals(value.Type, constant.Type);
				case MemberNode member:
					var reference = Unwrapped(operation) ?? model.GetOperation(Bare(syntax), cancellationToken);

					return reference switch
					{
						IFieldReferenceOperation { Instance: null } field =>
							SymbolEqualityComparer.Default.Equals(field.Field.OriginalDefinition, member.Member),
						IPropertyReferenceOperation { Instance: null } property =>
							SymbolEqualityComparer.Default.Equals(property.Property.OriginalDefinition, member.Member),
						_ => false,
					};
				default:
					return false;
			}
		}

		/// <summary>
		/// Captures <paramref name="syntax"/> for a placeholder, once it meets the placeholder's type. The
		/// type checked is the expression's own, not the one it is converted to at the site, so a
		/// <c>char</c> does not satisfy <c>:string</c> for having been passed where an object goes; an
		/// expression with no type of its own -- a lambda, a null -- is checked by what it converts to.
		/// </summary>
		private bool Placeholder(PlaceholderNode placeholder, ExpressionSyntax syntax)
		{
			if (placeholder.Constraint is { } constraint)
			{
				var info = model.GetTypeInfo(syntax, cancellationToken);
				var type = info.Type ?? info.ConvertedType;

				if (type is null || !model.Compilation.ClassifyConversion(type, constraint).IsImplicit) return false;
			}

			_captures[placeholder.Name] = new Capture(placeholder.Name, PlaceholderKind.Expression, syntax, default);

			return true;
		}

		/// <summary>Whether the site is a built-in logical not of something the find's operand matches.</summary>
		private bool Not(NotNode node, ExpressionSyntax syntax, IOperation? operation)
		{
			if (Bare(syntax) is not PrefixUnaryExpressionSyntax prefix || !prefix.IsKind(SyntaxKind.LogicalNotExpression)) return false;

			var unary = (Unwrapped(operation) ?? model.GetOperation(prefix, cancellationToken)) as IUnaryOperation;

			return unary is { OperatorKind: UnaryOperatorKind.Not, OperatorMethod: null }
				&& Expression(node.Operand, prefix.Operand, unary.Operand);
		}

		/// <summary>Captures a lambda of one parameter that is not async: the parameter's identifier, and its body.</summary>
		private bool Lambda(LambdaNode node, ExpressionSyntax syntax)
		{
			if (Bare(syntax) is not LambdaExpressionSyntax lambda || lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)) return false;

			var parameter = lambda switch
			{
				SimpleLambdaExpressionSyntax simple => simple.Parameter,
				ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => parenthesized.ParameterList.Parameters[0],
				_ => null,
			};

			if (parameter is null || parameter.Modifiers.Count > 0) return false;

			_captures[node.Parameter] = new Capture(node.Parameter, PlaceholderKind.Identifier, null, parameter.Identifier);
			_captures[node.Body] = new Capture(node.Body, PlaceholderKind.Expression, lambda.Body, default);

			return true;
		}
	}
}
