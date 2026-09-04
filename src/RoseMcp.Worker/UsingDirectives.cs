using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
	/// The file with each namespace imported, and a report of what was already covered.
	/// </summary>
	/// <param name="root">The file as it stands.</param>
	/// <param name="model">Used to ask what is already in scope, which is not only what this file says.</param>
	/// <param name="namespaces">Namespaces to ensure, as written in a using directive.</param>
	/// <param name="style">What the file's own settings ask for.</param>
	/// <param name="cancellationToken">Cancels the scope lookups.</param>
	public static UsingInsertion Ensure(
		CompilationUnitSyntax root,
		SemanticModel model,
		IReadOnlyList<string> namespaces,
		UsingStyle style,
		CancellationToken cancellationToken)
	{
		var added = new List<string>();
		var covered = new List<string>();
		var current = root;

		foreach (var requested in namespaces.Select(Normalise).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (AlreadyInScope(current, model, requested, cancellationToken) is { } reason)
			{
				covered.Add($"{requested}: {reason}");
				continue;
			}

			current = Insert(current, requested, style);
			added.Add(requested);
		}

		return new UsingInsertion { Root = current, Added = added, AlreadyInScope = covered };
	}

	/// <summary>
	/// Why a namespace needs no directive, or null when it does.
	/// <para>
	/// Three ways to already be in scope, and only the first is visible in the file. The other two
	/// are why this is asked of the compilation rather than of the using list: adding a directive
	/// for something a global using already covers is IDE0005, which fails the build.
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
		string requested,
		CancellationToken cancellationToken)
	{
		if (root.Usings.Any(directive => Names(directive) == requested)) return "already imported here";

		if (Declared(root) is { Length: > 0 } declared && Encloses(requested, declared))
		{
			return $"in scope already, since this file is in namespace {declared}";
		}

		var position = root.Members.FirstOrDefault()?.SpanStart ?? root.Span.End;

		foreach (var scope in model.GetImportScopes(position, cancellationToken))
		{
			foreach (var import in scope.Imports)
			{
				if (import.NamespaceOrType.ToDisplayString() != requested) continue;

				return "in scope already, from a global or implicit using";
			}
		}

		return null;
	}

	/// <summary>
	/// The file with one directive written in where the file's own ordering puts it.
	/// </summary>
	private static CompilationUnitSyntax Insert(CompilationUnitSyntax root, string requested, UsingStyle style)
	{
		// The keyword needs its own space. SyntaxFactory gives it none, so a directive built the
		// obvious way renders as usingSystem.Text; -- which parses as a top-level statement and
		// reports four errors that say nothing about a missing space.
		var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(requested))
			.WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(SyntaxFactory.Space))
			.WithTrailingTrivia(SyntaxFactory.EndOfLine(style.LineEnding));

		var existing = root.Usings;
		var index = Position(existing, requested, style);

		// Starting a group of its own, and only where the file already separates them: this
		// repository's .editorconfig says not to and every file does anyway, so the file is the
		// better authority on what its own imports should look like.
		var separate = style.SeparateGroups
			&& index > 0
			&& Group(Names(existing[index - 1])) != Group(requested);

		if (separate) directive = directive.WithLeadingTrivia(SyntaxFactory.EndOfLine(style.LineEnding));

		// Going in first means inheriting whatever sat above the old first line -- the file header,
		// a copyright, an auto-generated marker -- because that belongs to the file and not to the
		// directive it happened to precede.
		if (index == 0) return WithNewFirst(root, directive, style);

		return root.WithUsings(existing.Insert(index, directive));
	}

	/// <summary>
	/// Inserts at the top, moving the leading trivia of whatever was there onto the new directive so
	/// the file header stays at the top of the file.
	/// </summary>
	private static CompilationUnitSyntax WithNewFirst(
		CompilationUnitSyntax root,
		UsingDirectiveSyntax directive,
		UsingStyle style)
	{
		var blank = SyntaxFactory.EndOfLine(style.LineEnding);

		if (root.Usings.Count > 0)
		{
			var displaced = root.Usings[0];
			var separate = style.SeparateGroups && Group(Names(displaced)) != Group(Names(directive));

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
	/// Where the directive goes: before the first one that sorts after it, or at the end.
	/// <para>
	/// Found by comparing rather than by sorting the list, because a list that is not already in
	/// order is not this call's business to fix -- reordering somebody's imports as a side effect of
	/// adding one is a diff nobody asked for.
	/// </para>
	/// </summary>
	private static int Position(SyntaxList<UsingDirectiveSyntax> existing, string requested, UsingStyle style)
	{
		for (var index = 0; index < existing.Count; index++)
		{
			// A using with an alias, or a static one, sorts by its own rules and is left where it is.
			if (existing[index].Alias is not null || existing[index].StaticKeyword != default) continue;

			if (Sorts(requested, Names(existing[index]), style) < 0) return index;
		}

		return existing.Count;
	}

	/// <summary>
	/// The order two imports go in: System first where the file asks for it, then ordinal.
	/// </summary>
	private static int Sorts(string left, string right, UsingStyle style)
	{
		if (style.SystemFirst)
		{
			var leftIsSystem = Group(left) == "System";
			var rightIsSystem = Group(right) == "System";

			if (leftIsSystem != rightIsSystem) return leftIsSystem ? -1 : 1;
		}

		return string.CompareOrdinal(left, right);
	}

	/// <summary>The first segment, which is what a group of imports has in common.</summary>
	private static string Group(string name)
	{
		var dot = name.IndexOf('.', StringComparison.Ordinal);

		return dot < 0 ? name : name[..dot];
	}

	/// <summary>True when <paramref name="candidate"/> is <paramref name="inner"/> or encloses it.</summary>
	private static bool Encloses(string candidate, string inner) =>
		string.Equals(candidate, inner, StringComparison.Ordinal)
			|| inner.StartsWith($"{candidate}.", StringComparison.Ordinal);

	private static string? Declared(CompilationUnitSyntax root) =>
		root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

	private static string Names(UsingDirectiveSyntax directive) => directive.Name?.ToString() ?? string.Empty;

	/// <summary>
	/// A namespace as a using directive spells it, however the caller wrote it: with the keyword, with
	/// the semicolon, or as the bare name.
	/// </summary>
	private static string Normalise(string requested)
	{
		var name = requested.Trim();

		if (name.StartsWith("using ", StringComparison.Ordinal)) name = name["using ".Length..].Trim();
		if (name.EndsWith(';')) name = name[..^1].Trim();

		return name;
	}
}
