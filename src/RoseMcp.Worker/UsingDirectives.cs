using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Puts a using directive where the file would have put it.
/// <para>
/// This exists because writing a member is not the whole job. The code an agent writes routinely
/// needs an import the file does not have, and until now the tools said so and stopped -- which put
/// the caller back in text-land at the moment they had just been talked out of it, for the most
/// common thing needed immediately after a successful semantic write. Measured at five occurrences
/// in one session of building these tools with themselves.
/// </para>
/// <para>
/// It looks trivial and is not, in a repository with any opinion about imports. Sort position,
/// whether System comes first, whether groups are separated by a blank line, the file header that
/// has to stay above everything, and the several ways a namespace can already be in scope without
/// appearing in this file at all -- a global using, an implicit using from the SDK, or simply being
/// the namespace the file is in. Every one of those is something the compilation knows and a splice
/// guesses at, and getting it wrong is IDE0005 or IDE0055, which are build errors here.
/// </para>
/// </summary>
public static class UsingDirectives
{
	/// <summary>
	/// The file with each import written in, and a report of what was already covered.
	/// </summary>
	/// <param name="root">The file as it stands.</param>
	/// <param name="model">Used to ask what is already in scope, which is not only what this file says.</param>
	/// <param name="namespaces">
	/// Imports to ensure, each as <see cref="ImportDirective.Parse"/> reads one. All of them are parsed
	/// before any is looked up, so an argument that is not an import is refused by name.
	/// </param>
	/// <param name="style">What the file's own settings ask for.</param>
	/// <param name="cancellationToken">Cancels the scope lookups.</param>
	/// <exception cref="ArgumentException">
	/// An argument is not an import, or is an alias whose name already stands for something else.
	/// </exception>
	public static UsingInsertion Ensure(
		CompilationUnitSyntax root,
		SemanticModel model,
		IReadOnlyList<string> namespaces,
		UsingStyle style,
		CancellationToken cancellationToken)
	{
		var imports = namespaces
			.Where(requested => !string.IsNullOrWhiteSpace(requested))
			.Select(ImportDirective.Parse)
			.DistinctBy(import => import.Text, StringComparer.Ordinal)
			.ToList();

		var added = new List<string>();
		var covered = new List<string>();
		var current = root;

		foreach (var import in imports)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (AlreadyInScope(current, model, import, cancellationToken) is { } reason)
			{
				covered.Add($"{import.Text}: {reason}");
				continue;
			}

			current = Insert(current, import, style);
			added.Add(import.Text);
		}

		return new UsingInsertion { Root = current, Added = added, AlreadyInScope = covered };
	}

	/// <summary>
	/// The part of <paramref name="root"/> that importing into it changes: the directives already
	/// there, or the place in front of what follows them where <see cref="Ensure"/> writes the first
	/// one when there are none.
	/// </summary>
	public static TextSpan Region(CompilationUnitSyntax root)
	{
		if (root.Usings.Count > 0) return TextSpan.FromBounds(root.Usings[0].SpanStart, root.Usings[^1].FullSpan.End);

		var following = root.AttributeLists.FirstOrDefault()?.SpanStart
			?? root.Members.FirstOrDefault()?.SpanStart
			?? root.EndOfFileToken.SpanStart;

		return new TextSpan(following, 0);
	}

	/// <summary>
	/// Why an import needs no directive, or null when it does.
	/// <para>
	/// Three ways to already be in scope, and only the first is visible in the file. The other two
	/// are why this is asked of the compilation rather than of the using list: adding a directive
	/// for something a global using already covers is IDE0005, which fails the build.
	/// </para>
	/// <para>
	/// Like is compared with like. A namespace import does not cover a static import of a type inside
	/// it, and a static import of a type is not an import of a namespace with the same name, so the
	/// kind is matched as well as the name. An alias whose name already stands for something else is
	/// refused rather than reported, since writing it is a compile error and not writing it leaves
	/// the name meaning something the caller did not ask for.
	/// </para>
	/// <para>
	/// Public because resolving a name asks the same question from the other end: a candidate
	/// namespace already in scope is one that would fix nothing, and that is worth saying rather
	/// than filtering out. Two implementations of "already in scope" would be two chances to
	/// disagree about what counts.
	/// </para>
	/// </summary>
	public static string? AlreadyInScope(
		CompilationUnitSyntax root,
		SemanticModel model,
		ImportDirective requested,
		CancellationToken cancellationToken)
	{
		var present = root.Usings.Select(ImportDirective.From).ToList();

		if (present.Any(import => import.Text == requested.Text)) return "already imported here";

		var isAlias = requested.Kind == ImportKind.Alias;

		if (isAlias && present.FirstOrDefault(import => import.Alias == requested.Alias) is { } clash)
		{
			throw AliasTaken(requested, clash.Target, "in this file");
		}

		var isNamespace = requested.Kind == ImportKind.Namespace;

		if (isNamespace && Declared(root) is { Length: > 0 } declared && Encloses(requested.Target, declared))
		{
			return $"in scope already, since this file is in namespace {declared}";
		}

		var position = root.Members.FirstOrDefault()?.SpanStart ?? root.Span.End;

		foreach (var scope in model.GetImportScopes(position, cancellationToken))
		{
			if (isAlias)
			{
				if (scope.Aliases.FirstOrDefault(alias => alias.Name == requested.Alias) is not { } existing) continue;

				var target = existing.Target.ToDisplayString();

				if (target == requested.Target) return "in scope already, from a global using";

				throw AliasTaken(requested, target, "through a global using");
			}

			if (scope.Imports.Any(import => Covers(import.NamespaceOrType, requested)))
			{
				return "in scope already, from a global or implicit using";
			}
		}

		return null;
	}

	/// <summary>
	/// The first segment of a name, which is what a group of namespace imports has in common.
	/// </summary>
	internal static string Group(string name)
	{
		var dot = name.IndexOf('.', StringComparison.Ordinal);

		return dot < 0 ? name : name[..dot];
	}

	/// <summary>
	/// The order two imports of the same kind go in: System first where the file asks for it, then
	/// ordinal.
	/// <para>
	/// Public because a file that does not exist yet has its imports written as text rather than
	/// placed among existing ones, and two orderings would be two chances to disagree -- which is
	/// exactly what happened: a new file opened with its imports sorted ordinally, so anything
	/// alphabetically before "System" landed above it.
	/// </para>
	/// </summary>
	/// <param name="left">The import being placed.</param>
	/// <param name="right">The import it is being compared against.</param>
	/// <param name="systemFirst">Whether System imports sort above the rest.</param>
	public static int Sorts(string left, string right, bool systemFirst)
	{
		if (systemFirst)
		{
			var leftIsSystem = Group(left) == "System";
			var rightIsSystem = Group(right) == "System";

			if (leftIsSystem != rightIsSystem) return leftIsSystem ? -1 : 1;
		}

		return string.CompareOrdinal(left, right);
	}

	/// <summary>
	/// The file with one directive written in where the file's own ordering puts it.
	/// </summary>
	private static CompilationUnitSyntax Insert(CompilationUnitSyntax root, ImportDirective requested, UsingStyle style)
	{
		var directive = requested.ToSyntax(style.LineEnding);

		var existing = root.Usings;
		var index = Position(existing, requested, style);

		// Starting a group of its own, and only where the file already separates them: this
		// repository's .editorconfig says not to and every file does anyway, so the file is the
		// better authority on what its own imports should look like.
		var separate = style.SeparateGroups
			&& index > 0
			&& ImportDirective.From(existing[index - 1]).Group != requested.Group;

		if (separate) directive = directive.WithLeadingTrivia(SyntaxFactory.EndOfLine(style.LineEnding));

		// Going in first means inheriting whatever sat above the old first line -- the file header,
		// a copyright, an auto-generated marker -- because that belongs to the file and not to the
		// directive it happened to precede.
		if (index == 0) return WithNewFirst(root, directive, requested.Group, style);

		// Going in at the front of a group the file already separates: the blank line belongs above
		// the group, so it moves up to the new first directive rather than being written twice and
		// cutting the group in half.
		var joinsSeparatedGroup = separate
			&& index < existing.Count
			&& ImportDirective.From(existing[index]).Group == requested.Group;

		if (joinsSeparatedGroup)
		{
			var displaced = existing[index];
			var trivia = displaced.GetLeadingTrivia().SkipWhile(item => item.IsKind(SyntaxKind.EndOfLineTrivia));

			existing = existing.Replace(displaced, displaced.WithLeadingTrivia(trivia));
		}

		return root.WithUsings(existing.Insert(index, directive));
	}

	/// <summary>
	/// Inserts at the top, moving the leading trivia of whatever was there onto the new directive so
	/// the file header stays at the top of the file.
	/// </summary>
	private static CompilationUnitSyntax WithNewFirst(
		CompilationUnitSyntax root,
		UsingDirectiveSyntax directive,
		string group,
		UsingStyle style)
	{
		var blank = SyntaxFactory.EndOfLine(style.LineEnding);

		if (root.Usings.Count > 0)
		{
			var displaced = root.Usings[0];
			var separate = style.SeparateGroups && ImportDirective.From(displaced).Group != group;

			return root.WithUsings(root.Usings
				.Replace(displaced, displaced.WithLeadingTrivia(separate ? [blank] : SyntaxFactory.TriviaList()))
				.Insert(0, directive.WithLeadingTrivia(displaced.GetLeadingTrivia())));
		}

		// No usings at all, so the namespace is what the header is attached to.
		if (root.Members.FirstOrDefault() is not { } first)
		{
			return root.WithUsings([directive]);
		}

		return root
			.WithMembers(root.Members.Replace(first, first.WithLeadingTrivia(blank)))
			.WithUsings([directive.WithLeadingTrivia(first.GetLeadingTrivia())]);
	}

	/// <summary>
	/// Where the directive goes: after its own kind's predecessors, before the first of its kind that
	/// sorts after it, or before the first of a kind that comes later.
	/// <para>
	/// Found by comparing rather than by sorting the list, because a list that is not already in
	/// order is not this call's business to fix -- reordering somebody's imports as a side effect of
	/// adding one is a diff nobody asked for.
	/// </para>
	/// <para>
	/// Kinds go in the order <see cref="ImportKind"/> declares them: namespaces, static imports,
	/// aliases. A plain import therefore stops in front of the first static one rather than walking
	/// past it to the end of the file, which would compile and trip no analyzer and is not what any
	/// file looks like. A global using is walked past whatever it is, because the compiler requires
	/// every one to come before the rest.
	/// </para>
	/// </summary>
	private static int Position(SyntaxList<UsingDirectiveSyntax> existing, ImportDirective requested, UsingStyle style)
	{
		for (var index = 0; index < existing.Count; index++)
		{
			var present = ImportDirective.From(existing[index]);

			if (present.Global) continue;
			if (present.Kind > requested.Kind) return index;

			var sortsBefore = present.Kind == requested.Kind
				&& Sorts(requested.SortKey, present.SortKey, style.SystemFirst) < 0;

			if (sortsBefore) return index;
		}

		return existing.Count;
	}

	/// <summary>
	/// Whether an import the compilation already has is the one asked for: the same name, and the
	/// same kind of symbol, since a plain import names a namespace and a static one names a type.
	/// </summary>
	private static bool Covers(INamespaceOrTypeSymbol imported, ImportDirective requested)
	{
		var sameKind = requested.Kind == ImportKind.Static
			? imported is ITypeSymbol
			: imported is INamespaceSymbol;

		return sameKind && imported.ToDisplayString() == requested.Target;
	}

	private static ArgumentException AliasTaken(ImportDirective requested, string target, string where) =>
		new($"Cannot import {requested.Text}: {requested.Alias} already stands for {target} {where}, and a "
			+ "second alias of the same name does not compile. Choose another name.");

	/// <summary>True when <paramref name="candidate"/> is <paramref name="inner"/> or encloses it.</summary>
	private static bool Encloses(string candidate, string inner) =>
		string.Equals(candidate, inner, StringComparison.Ordinal)
			|| inner.StartsWith($"{candidate}.", StringComparison.Ordinal);

	private static string? Declared(CompilationUnitSyntax root) =>
		root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
}
