using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace RoseMcp.Worker;

/// <summary>
/// Whether an instance member can move to another type without changing what it means, and the
/// refusal that says what was found when it cannot.
/// <para>
/// Moving an instance member is safe in one shape: nothing refers to it, so no call site needs a
/// receiver of the new type, and nothing in it reads the instance it leaves that the instance it
/// arrives in does not also have. A test method moving between fixtures is that shape -- the runner
/// finds it by attribute, and its body reads nothing of the fixture. Every other instance member is
/// refused, with the reference, the name or the line that decided it.
/// </para>
/// <para>
/// Checked twice. Before the move, against the member as written: what kind of member it is,
/// whether anything refers to it, whether it names <c>this</c> or <c>base</c>, and which members of
/// its own type it reaches through an implicit <c>this</c>. After the move and before anything is
/// written, by binding the member where it landed and comparing every name in it with what that name
/// meant before. Only the second sees a call that quietly resolves to a same-named member of the new
/// type, an overload the new type adds, or an extension method the new file imports -- each of which
/// compiles, so no verification afterwards would say a word.
/// </para>
/// </summary>
internal static class InstanceMove
{
	/// <summary>
	/// How a symbol is told apart from every other across two compilations of one solution: kind,
	/// assembly and the whole signature, type arguments included, so <c>Base&lt;int&gt;.Shared</c> and
	/// <c>Base&lt;string&gt;.Shared</c> are different answers.
	/// </summary>
	private static readonly SymbolDisplayFormat KeyFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		memberOptions: SymbolDisplayMemberOptions.IncludeParameters
			| SymbolDisplayMemberOptions.IncludeContainingType
			| SymbolDisplayMemberOptions.IncludeExplicitInterface
			| SymbolDisplayMemberOptions.IncludeType,
		parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

	/// <summary>
	/// The refusals that need nothing but the two declarations: a member that is not the kind that can
	/// move as an instance member at all, and a type that cannot take one.
	/// </summary>
	public static void GuardShape(DeclarationTarget source, TypeTarget target)
	{
		var symbol = source.Symbol;
		var signature = source.Signature;

		if (target.Symbol.IsStatic)
		{
			throw new ArgumentException(
				$"{target.Symbol.Name} is static, and {signature} is an instance member, which a static class cannot declare.");
		}

		if (target.Symbol.TypeKind == TypeKind.Interface)
		{
			throw new ArgumentException(
				$"{target.Symbol.Name} is an interface, where an instance member with a body becomes a default "
					+ $"implementation every implementer inherits. Moving {signature} there is a different change.");
		}

		var kind = symbol switch
		{
			IMethodSymbol { MethodKind: MethodKind.Ordinary } => null,
			IMethodSymbol method => KindOf(method.MethodKind),
			IPropertySymbol { IsIndexer: true } => "an indexer",
			IPropertySymbol or IFieldSymbol or IEventSymbol => null,
			INamedTypeSymbol => "a nested type",
			_ => $"a {symbol.Kind.ToString().ToLowerInvariant()}",
		};

		if (kind is not null)
		{
			throw new ArgumentException(
				$"{signature} is {kind}, which belongs to the type that declares it and cannot move to another as an instance member.");
		}

		var dispatched = symbol.IsOverride ? "an override, so calls to the member it overrides reach it"
			: symbol.IsAbstract ? "abstract, so every call reaches it through an override"
			: symbol.IsVirtual ? "virtual, so a call to it may reach an override instead"
			: null;

		if (dispatched is not null)
		{
			throw new ArgumentException(
				$"{signature} is {dispatched} -- calls a search for its references does not count, and moving it "
					+ "would change what they reach. Move it by hand.");
		}

		if (AccessibilityModifiers.ExplicitlyImplemented(symbol) is { } explicitly)
		{
			throw new ArgumentException(
				$"{signature} implements {explicitly.ToDisplayString()}, so it is called through the interface, which "
					+ "a search for its references does not count. Move it by hand.");
		}

		if (source.Declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			throw new ArgumentException(
				$"{signature} is partial, and moving one of its declarations would leave the other behind.");
		}

		if (source.Declaration is BaseFieldDeclarationSyntax { Declaration.Variables.Count: > 1 } field)
		{
			var names = string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.Text));

			throw new ArgumentException(
				$"{signature} is declared in one statement with others ({names}), and moving the statement moves "
					+ "them all. Split the declaration first, with rose_replace_member.");
		}
	}

	/// <summary>
	/// The references that are calls from somewhere else. A call from inside the member is a call to
	/// itself, which goes where the member goes and needs no rewriting.
	/// </summary>
	public static IReadOnlyList<Location> Outside(IReadOnlyList<Location> sites, DeclarationTarget source) =>
		[
			.. sites.Where(site => site.SourceTree != source.Declaration.SyntaxTree
				|| !source.Declaration.FullSpan.Contains(site.SourceSpan)),
		];

	/// <summary>
	/// The refusals that need the solution: a member something refers to, one called through an
	/// interface its own type or a derived type implements, and one that reads the instance it is
	/// leaving in a way the type it goes to cannot answer.
	/// </summary>
	public static async Task GuardAsync(
		Solution solution,
		DeclarationTarget source,
		TypeTarget target,
		IReadOnlyList<Location> outside,
		CancellationToken cancellationToken)
	{
		var signature = source.Signature;

		if (outside.Count > 0)
		{
			var first = outside[0].GetLineSpan();

			throw new ArgumentException(
				$"{signature} is an instance member with {outside.Count} reference(s), the first in "
					+ $"{Path.GetFileName(first.Path)} at line {first.StartLinePosition.Line + 1}, and moving one changes "
					+ "what 'this' means inside it -- every call site would need a receiver it has no reason to have "
					+ "to hand. Make it static first, with rose_replace_member, or move it by hand.");
		}

		if (await InterfaceCallAsync(solution, source.Symbol, cancellationToken) is { } called)
		{
			throw new ArgumentException(
				$"{signature} implements {called}, so it is called through the interface, which a search for its "
					+ "references does not count. Moving it would leave that type without it. Move it by hand.");
		}

		var model = await source.Document.GetSemanticModelAsync(cancellationToken)
			?? throw new InvalidOperationException($"{source.Document.Name} has no semantic model to read the member in.");

		var member = DeclaredSymbol(model, source.Declaration, cancellationToken)
			?? throw new InvalidOperationException($"The compiler declares nothing at {signature}.");

		if (InstanceReads(model, source.Declaration, member, target.Symbol, cancellationToken) is { } read)
		{
			throw new ArgumentException(
				$"{signature} {read}. An instance member moves only when nothing refers to it and it reads "
					+ $"nothing of {member.ContainingType.Name} that {target.Symbol.Name} does not also have. Make it "
					+ "static first, with rose_replace_member, or move it by hand.");
		}
	}

	/// <summary>
	/// The last check, made on the moved solution before anything is written: every name in the member
	/// binds where it landed to what it bound to where it was, and the member itself, in its new type,
	/// neither hides an inherited member nor becomes an interface's implementation.
	/// <para>
	/// A name that binds to nothing is let through. That fails to compile, so the verification after
	/// the write names it, as it does for a static member moved away from an import it needs. A name
	/// that binds to something else compiles and means something else, which is the failure this
	/// exists to stop.
	/// </para>
	/// </summary>
	public static async Task ConfirmAsync(
		Solution before,
		Solution after,
		DeclarationTarget source,
		TypeTarget target,
		CancellationToken cancellationToken)
	{
		var oldModel = await source.Document.GetSemanticModelAsync(cancellationToken);
		var oldMember = oldModel is null ? null : DeclaredSymbol(oldModel, source.Declaration, cancellationToken);

		var (newModel, landed) = await LandedAsync(before, after, target, cancellationToken);
		var newMember = DeclaredSymbol(newModel, landed, cancellationToken);

		if (oldModel is null || oldMember is null || newMember is null || newMember.ContainingType is not { } home)
		{
			throw Lost(source);
		}

		var oldNodes = source.Declaration.DescendantNodesAndSelf().ToArray();
		var newNodes = landed.DescendantNodesAndSelf().ToArray();

		var sameShape = oldNodes.Length == newNodes.Length
			&& oldNodes.Zip(newNodes).All(pair => pair.First.RawKind == pair.Second.RawKind);

		if (!sameShape) throw Lost(source);

		for (var index = 0; index < oldNodes.Length; index++)
		{
			var was = Bound(oldModel, oldNodes[index], cancellationToken);
			if (was is null) continue;

			var now = Bound(newModel, newNodes[index], cancellationToken);
			var line = LineOf(oldNodes[index]);

			if (Same(was, oldMember))
			{
				if (now is not null && Same(now, newMember)) continue;

				throw new ArgumentException(
					$"{source.Signature} refers to itself at line {line} in a way that would not reach the moved "
						+ $"member in {target.Symbol.Name}. Move it by hand.");
			}

			if (Inside(was, oldMember)) continue;

			if (was is ITypeParameterSymbol)
			{
				throw new ArgumentException(
					$"{source.Signature} uses {was.Name}, a type parameter of {oldMember.ContainingType.Name}, at line "
						+ $"{line}, which means nothing in {target.Symbol.Name}. Move it by hand.");
			}

			if (now is null || Key(was) == Key(now)) continue;

			throw new ArgumentException(
				$"'{oldNodes[index]}' at line {line} of {source.Signature} means {was.ToDisplayString()} where it is, and "
					+ $"would mean {now.ToDisplayString()} in {target.Symbol.Name} -- it compiles either way, and does "
					+ "something else. Nothing was written; qualify the name first, or move it by hand.");
		}

		if (Collision(home, newMember) is { } collision)
		{
			throw new ArgumentException(
				$"In {target.Symbol.Name}, {source.Signature} would {collision}, so calls that reach that one would "
					+ "reach the moved member instead. Nothing was written; move it by hand.");
		}
	}

	/// <summary>
	/// The first way the member reads the instance it is declared in that the target cannot answer,
	/// as a clause to follow its name, or null when there is none. Lambdas and local functions are
	/// walked too, since a <c>this</c> they capture is the member's.
	/// </summary>
	private static string? InstanceReads(
		SemanticModel model,
		MemberDeclarationSyntax declaration,
		ISymbol member,
		INamedTypeSymbol target,
		CancellationToken cancellationToken)
	{
		var roots = declaration.DescendantNodesAndSelf()
			.Select(node => model.GetOperation(node, cancellationToken))
			.Where(operation => operation is { Parent: null })
			.Select(operation => operation!);

		foreach (var operation in roots.SelectMany(root => root.DescendantsAndSelf()))
		{
			var line = LineOf(operation.Syntax);

			switch (operation)
			{
				case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } reference:
				{
					if (!reference.IsImplicit) return $"names '{reference.Syntax}' at line {line}";

					var reached = reference.Parent switch
					{
						IMemberReferenceOperation access when access.Instance == reference => access.Member,
						IInvocationOperation call when call.Instance == reference => call.TargetMethod,
						_ => null,
					};

					if (reached is null) return $"uses 'this' implicitly at line {line}";
					if (Inside(reached, member)) continue;
					if (Inherits(target, reached.ContainingType)) continue;

					return $"reads {reached.ContainingType.Name}.{reached.Name} at line {line} through an implicit 'this', "
						+ $"and {target.Name} has no such member to answer it";
				}

				case IParameterReferenceOperation parameter when !Inside(parameter.Parameter, member):
					return parameter.Parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor }
						? $"reads {parameter.Parameter.Name} at line {line}, a parameter of "
							+ $"{member.ContainingType.Name}'s primary constructor, which {target.Name} does not have"
						: $"reads {parameter.Parameter.Name} at line {line}, a parameter declared outside it, by "
							+ $"{parameter.Parameter.ContainingSymbol.ToDisplayString()}";
			}
		}

		return null;
	}

	/// <summary>
	/// The interface member that reaches <paramref name="symbol"/> through its own type or through a
	/// type derived from it, which is a call no reference search counts as one to this member.
	/// </summary>
	private static async Task<string?> InterfaceCallAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
	{
		if (AccessibilityModifiers.ImplicitlyImplemented(symbol) is { } own) return own.ToDisplayString();

		if (symbol.ContainingType is not { TypeKind: TypeKind.Class } type) return null;

		var derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken);

		foreach (var subtype in derived)
		{
			var implemented = subtype.AllInterfaces
				.SelectMany(@interface => @interface.GetMembers(symbol.Name))
				.FirstOrDefault(candidate => subtype.FindImplementationForInterfaceMember(candidate) is { } found
					&& SameDeclaration(found, symbol));

			if (implemented is not null) return $"{implemented.ToDisplayString()} for {subtype.Name}";
		}

		return null;
	}

	/// <summary>
	/// Whether the moved member, where it now is, hides a member the target inherits or becomes the
	/// implementation of one of its interfaces' members -- either of which changes what an existing
	/// call reaches, with nothing at the member to show it.
	/// </summary>
	private static string? Collision(INamedTypeSymbol home, ISymbol moved)
	{
		var implemented = home.AllInterfaces
			.SelectMany(@interface => @interface.GetMembers(moved.Name))
			.FirstOrDefault(candidate => home.FindImplementationForInterfaceMember(candidate) is { } found && Same(found, moved));

		if (implemented is not null) return $"become the implementation of {implemented.ToDisplayString()}";

		for (var type = home.BaseType; type is not null; type = type.BaseType)
		{
			var hidden = type.GetMembers(moved.Name).FirstOrDefault(inherited =>
				!inherited.IsImplicitlyDeclared
					&& inherited.DeclaredAccessibility != Accessibility.Private
					&& SameParameters(inherited, moved));

			if (hidden is not null) return $"hide {hidden.ToDisplayString()}, which it inherits";
		}

		return null;
	}

	/// <summary>
	/// Whether two members of one name would clash: any two that are not both methods, and two methods
	/// only when they take the same parameters, passed the same way.
	/// </summary>
	private static bool SameParameters(ISymbol left, ISymbol right)
	{
		if (left is not IMethodSymbol first || right is not IMethodSymbol second) return true;
		if (first.Parameters.Length != second.Parameters.Length) return false;

		return first.Parameters.Zip(second.Parameters).All(pair =>
			pair.First.RefKind == pair.Second.RefKind
				&& SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type));
	}

	/// <summary>
	/// The moved declaration in the moved solution, and the model to bind it with. It is the last member
	/// of the declaration it was put into, which is found as the same one of the target's declarations
	/// in that document as it was before: the write has replaced the text, so no annotation survives.
	/// </summary>
	private static async Task<(SemanticModel Model, MemberDeclarationSyntax Landed)> LandedAsync(
		Solution before,
		Solution after,
		TypeTarget target,
		CancellationToken cancellationToken)
	{
		var oldDocument = before.GetDocument(target.Document.Id);
		var newDocument = after.GetDocument(target.Document.Id);

		var oldModel = oldDocument is null ? null : await oldDocument.GetSemanticModelAsync(cancellationToken);
		var newModel = newDocument is null ? null : await newDocument.GetSemanticModelAsync(cancellationToken);

		if (oldModel is null || newModel is null) throw LostTarget(target);

		var key = Key(target.Symbol);

		var oldParts = PartsOf(oldModel, key, cancellationToken);
		var newParts = PartsOf(newModel, key, cancellationToken);

		var position = oldParts.FindIndex(part => part.Span == target.Declaration.Span);

		if (position < 0 || position >= newParts.Count || newParts[position].Members.Count == 0) throw LostTarget(target);

		return (newModel, newParts[position].Members[^1]);
	}

	private static List<TypeDeclarationSyntax> PartsOf(SemanticModel model, string key, CancellationToken cancellationToken) =>
		[
			.. model.SyntaxTree.GetRoot(cancellationToken)
				.DescendantNodes()
				.OfType<TypeDeclarationSyntax>()
				.Where(type => model.GetDeclaredSymbol(type, cancellationToken) is { } declared && Key(declared) == key),
		];

	/// <summary>
	/// What a node names, where it names one thing. A node that binds to several candidates or to
	/// nothing says nothing about what the move could change.
	/// </summary>
	private static ISymbol? Bound(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken) =>
		model.GetSymbolInfo(node, cancellationToken).Symbol;

	/// <summary>The symbol a declaration declares; for a field or an event field, its one variable.</summary>
	private static ISymbol? DeclaredSymbol(SemanticModel model, MemberDeclarationSyntax declaration, CancellationToken cancellationToken) =>
		declaration is BaseFieldDeclarationSyntax { Declaration.Variables: [var variable, ..] }
			? model.GetDeclaredSymbol(variable, cancellationToken)
			: model.GetDeclaredSymbol(declaration, cancellationToken);

	/// <summary>
	/// Whether a symbol is declared within the member, or is the member: its parameters and locals,
	/// lambdas and local functions with theirs, its own type parameters, and the accessors and backing
	/// field of a property. Those move with it and mean the same wherever it goes.
	/// </summary>
	private static bool Inside(ISymbol symbol, ISymbol member)
	{
		for (var current = symbol; current is not null; current = current.ContainingSymbol)
		{
			if (Same(current, member)) return true;

			var associated = current switch
			{
				IMethodSymbol method => method.AssociatedSymbol,
				IFieldSymbol field => field.AssociatedSymbol,
				_ => null,
			};

			if (associated is not null && Same(associated, member)) return true;
		}

		return false;
	}

	/// <summary>Whether an instance of <paramref name="target"/> has the members <paramref name="type"/> declares.</summary>
	private static bool Inherits(INamedTypeSymbol target, INamedTypeSymbol type)
	{
		var wanted = Key(type);

		for (INamedTypeSymbol? current = target; current is not null; current = current.BaseType)
		{
			if (Key(current) == wanted) return true;
		}

		return false;
	}

	private static bool Same(ISymbol left, ISymbol right) =>
		SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

	/// <summary>
	/// Whether two symbols, possibly from different compilations of the same code, are one declaration.
	/// </summary>
	private static bool SameDeclaration(ISymbol left, ISymbol right)
	{
		var first = left.OriginalDefinition.Locations.FirstOrDefault(location => location.IsInSource);
		var second = right.OriginalDefinition.Locations.FirstOrDefault(location => location.IsInSource);

		return first is not null
			&& second is not null
			&& first.SourceSpan == second.SourceSpan
			&& string.Equals(first.SourceTree?.FilePath, second.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	private static string Key(ISymbol symbol) =>
		$"{symbol.Kind}|{(symbol is INamespaceSymbol ? string.Empty : symbol.ContainingAssembly?.Identity.Name)}|"
			+ symbol.ToDisplayString(KeyFormat);

	private static int LineOf(SyntaxNode node) =>
		node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

	private static string KindOf(MethodKind kind) => kind switch
	{
		MethodKind.Constructor or MethodKind.StaticConstructor => "a constructor",
		MethodKind.Destructor => "a finalizer",
		MethodKind.UserDefinedOperator or MethodKind.Conversion => "an operator",
		MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => "an accessor",
		_ => $"a {kind} method",
	};

	/// <summary>
	/// The moved member could not be found again in the solution the move produced. Never the caller's
	/// error, and never survivable: a move that cannot be checked is not written.
	/// </summary>
	private static InvalidOperationException Lost(DeclarationTarget source) =>
		new($"The move lost track of {source.Signature} once it had moved it, so it could not check what the "
			+ "member binds to there, and nothing was written.");

	private static InvalidOperationException LostTarget(TypeTarget target) =>
		new($"The move lost track of {target.Symbol.Name} once it had moved the member into it, so nothing was written.");
}
