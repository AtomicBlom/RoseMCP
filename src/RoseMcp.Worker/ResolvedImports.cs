using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Turns the unresolved names an edit introduced into imports, adding the ones with a single
/// answer and reporting the rest.
/// <para>
/// <see cref="MissingImports"/> already works out what would import a name, off the compilation
/// that has just been built to find the errors. The step it stops short of is applying the answer,
/// and stopping there hands the caller back a round trip at exactly the moment they were promised
/// there would not be one -- a file the tool has just written, and a second call to make it
/// compile.
/// </para>
/// <para>
/// What it must not do is choose. Plenty of names live in two namespaces at once, and the wrong
/// import is the worst kind of wrong here because it compiles and binds to the wrong type. So the
/// answer is one namespace or a list, and a name whose only other home is a project this one does
/// not reference counts as a list -- see
/// <c>docs/decisions/automatic-imports-need-a-name-to-be-unique-everywhere.md</c>.
/// </para>
/// </summary>
public static class ResolvedImports
{
	/// <summary>
	/// The imports for one file's unresolved names, sorted into what can be added, what cannot be
	/// chosen, and what no import would fix.
	/// </summary>
	/// <param name="snapshot">The solution as the edit leaves it.</param>
	/// <param name="introduced">The errors the edit brought into being.</param>
	/// <param name="filePath">The file the imports would go into.</param>
	/// <param name="limit">
	/// How many distinct names to look up. A member edit that introduces forty unresolved names has
	/// gone wrong in a way no import list will fix; a new file with ten is an ordinary new class,
	/// which is why the caller sets this rather than the search.
	/// </param>
	/// <param name="cancellationToken">Cancels the lookups.</param>
	public static async Task<Imports> ForAsync(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<DiagnosticEntry> introduced,
		string filePath,
		int limit,
		CancellationToken cancellationToken)
	{
		var add = new List<string>();
		var applied = new List<AppliedImport>();
		var ambiguous = new List<string>();
		var unresolved = new List<string>();
		var asked = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in introduced)
		{
			if (!MissingImports.IsUnresolved(entry.Id)) continue;
			if (entry.FilePath is not { Length: > 0 } path) continue;
			if (!SamePath(path, filePath)) continue;
			if (asked.Count >= limit) break;

			var unresolvedAt = await MissingImports.UnresolvedAtAsync(
				snapshot.Solution, path, entry.Line, entry.Column, cancellationToken);

			if (unresolvedAt is not { } at || !asked.Add(at.Name)) continue;

			var (name, use) = at;

			// How the name is used rules out kinds of candidate before any is counted: a namespace
			// holding a type called Group is no answer to a call to Group(...), and counting it as the
			// sole candidate imports it.
			var resolution = await NameResolver.ResolveAsync(
				snapshot, new ResolveNameRequest { Name = name, FilePath = path, Use = use }, cancellationToken);

			var usable = resolution.Candidates
				.Where(candidate => candidate.AlreadyInScope is null && candidate.Caveat is null)
				.ToArray();

			var spaces = usable
				.Select(candidate => candidate.Namespace)
				.Distinct(StringComparer.Ordinal)
				.ToArray();

			if (spaces.Length > 1)
			{
				ambiguous.Add($"{name} is in {spaces.Length} namespaces ({string.Join(", ", spaces)}); "
					+ "nothing was imported, because importing the wrong one compiles. Pass the one you mean.");

				continue;
			}

			if (resolution.Import is { } single && spaces.Length == 1)
			{
				// A name that also exists somewhere this project cannot reach is a choice, not an
				// answer: the reachable one may not be the one that was meant, and adding it binds
				// silently to the wrong type.
				var elsewhere = resolution.Candidates
					.Where(candidate => candidate.Caveat is not null)
					.Select(candidate => candidate.Namespace)
					.Where(space => !string.Equals(space, single, StringComparison.Ordinal))
					.Distinct(StringComparer.Ordinal)
					.ToArray();

				if (elsewhere.Length > 0)
				{
					ambiguous.Add($"{name} is in {single} and also in {string.Join(", ", elsewhere)}, which this "
						+ "project does not reference. Nothing was imported: the reachable one may not be the "
						+ "one meant, and it would bind without complaint.");

					continue;
				}

				add.Add(single);
				applied.Add(new AppliedImport(name, single));

				continue;
			}

			unresolved.Add(resolution.Candidates is [{ Caveat: { } caveat } sole]
				? $"{name} is {sole.Symbol}, {caveat}."
				: use.WhyNothingFits(name) ?? $"{name} resolves to nothing in scope, and no import would fix it.");
		}

		return new Imports(add, applied, ambiguous, unresolved);
	}

	/// <summary>
	/// The solution with those namespaces imported into one document, in whatever order and grouping
	/// the file itself uses.
	/// </summary>
	/// <param name="solution">The solution as the edit leaves it.</param>
	/// <param name="id">The document to import into.</param>
	/// <param name="namespaces">Namespaces to ensure, each already known to have one answer.</param>
	/// <param name="rules">
	/// The document's layout, read from it before the edit, so an import is written in the ending the
	/// rest of the edit was.
	/// </param>
	/// <param name="cancellationToken">Cancels the scope lookups.</param>
	public static async Task<Solution> ApplyAsync(
		Solution solution,
		DocumentId id,
		IReadOnlyList<string> namespaces,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		if (namespaces.Count == 0 || solution.GetDocument(id) is not { } document) return solution;

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);

		if (model is null || tree is null) return solution;
		if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root) return solution;

		var style = UsingStyle.For(document.Project, tree, root, rules.LineEnding);

		var insertion = UsingDirectives.Ensure(root, model, namespaces, style, cancellationToken);

		return insertion.Added.Count == 0
			? solution
			: solution.WithDocumentSyntaxRoot(id, insertion.Root);
	}

	/// <summary>
	/// The solution with the imports applied, less any that did not bind the name it was fetched for.
	/// <para>
	/// Checked before anything is written, against the document with the imports in place, because an
	/// import that leaves its own name failing is the wrong import: "the only namespace anything of
	/// that name is in" was true and was not the namespace this code needs. Kept, it is a second error
	/// -- IDE0005, an unnecessary using, which is a build error here -- in a line the caller never
	/// wrote, on top of the one it was fetched to fix. Taken back out, the caller is left with the
	/// error they had and a sentence saying no import fixes it.
	/// </para>
	/// </summary>
	/// <param name="solution">The solution as the edit leaves it.</param>
	/// <param name="id">The document to import into.</param>
	/// <param name="imports">What the unresolved names turned out to want.</param>
	/// <param name="rules">The document's layout, read from it before the edit.</param>
	/// <param name="cancellationToken">Cancels the lookups and the binding.</param>
	public static async Task<(Solution Solution, Imports Imports)> ApplyResolvingAsync(
		Solution solution,
		DocumentId id,
		Imports imports,
		WhitespaceRules rules,
		CancellationToken cancellationToken)
	{
		if (!imports.AnythingToAdd) return (solution, imports);

		var applied = await ApplyAsync(solution, id, imports.Namespaces, rules, cancellationToken);
		var failing = await FailingNamesAsync(applied, id, cancellationToken);

		var kept = imports.Applied.Where(import => !failing.Contains(import.Name)).ToArray();

		if (kept.Length == imports.Applied.Count) return (applied, imports);

		var namespaces = kept.Select(import => import.Namespace).Distinct(StringComparer.Ordinal).ToArray();

		var dropped = imports.Applied
			.Where(import => failing.Contains(import.Name))
			.Select(import => namespaces.Contains(import.Namespace, StringComparer.Ordinal)
				? $"{import.Name}: importing {import.Namespace}, the only namespace anything of that name is in, did "
					+ $"not resolve {import.Name}. The import stays, for the other names it does resolve."
				: $"{import.Name}: importing {import.Namespace}, the only namespace anything of that name is in, did "
					+ $"not resolve {import.Name}, so it was taken back out. {import.Name} resolves to nothing in "
					+ "scope, and no import would fix it.");

		var reapplied = await ApplyAsync(solution, id, namespaces, rules, cancellationToken);

		return (reapplied, imports with
		{
			Namespaces = namespaces,
			Applied = kept,
			Unresolved = [.. imports.Unresolved, .. dropped],
		});
	}

	/// <summary>
	/// The three answers, kept apart on purpose. Folding ambiguous into unresolved would tell a
	/// caller a type does not exist when the problem is that it exists twice, which sends them off
	/// to write one.
	/// </summary>
	/// <param name="Namespaces">Namespaces to import, each with exactly one answer behind it.</param>
	/// <param name="Applied">
	/// The name each import was fetched for, beside the namespace it got. A pairing rather than a
	/// sentence, because whether the import works is only known once the document has been bound with
	/// it in place, and an import that leaves its own name failing is taken back out.
	/// </param>
	/// <param name="Ambiguous">Names with more than one candidate; nothing was imported for these.</param>
	/// <param name="Unresolved">Names no import would fix, with why not.</param>
	public sealed record Imports(
		IReadOnlyList<string> Namespaces,
		IReadOnlyList<AppliedImport> Applied,
		IReadOnlyList<string> Ambiguous,
		IReadOnlyList<string> Unresolved)
	{
		public static readonly Imports None = new([], [], [], []);

		public bool AnythingToAdd => Namespaces.Count > 0;
	}

	private static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

	/// <summary>One import, and the unresolved name it was fetched for.</summary>
	/// <param name="Name">The name that did not bind, which is what makes the import checkable.</param>
	/// <param name="Namespace">The single namespace anything of that name is in.</param>
	public readonly record struct AppliedImport(string Name, string Namespace);

	/// <summary>
	/// One sentence per import that was applied, naming the name it was fetched for. Every one of
	/// them resolved that name, since <see cref="ApplyResolvingAsync"/> takes back any that did not.
	/// </summary>
	public static IReadOnlyList<string> Report(Imports imports) =>
	[
		.. imports.Applied.Select(import =>
			$"{import.Name}: imported {import.Namespace}, the only namespace anything of that name is in."),
	];

	/// <summary>
	/// The names in the document that still do not bind, read off its own diagnostics with the imports
	/// in place.
	/// </summary>
	private static async Task<IReadOnlySet<string>> FailingNamesAsync(
		Solution solution,
		DocumentId id,
		CancellationToken cancellationToken)
	{
		var failing = new HashSet<string>(StringComparer.Ordinal);

		if (solution.GetDocument(id) is not { } document) return failing;

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var root = await document.GetSyntaxRootAsync(cancellationToken);

		if (model is null || root is null) return failing;

		foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken))
		{
			if (!StillFailing.Contains(diagnostic.Id, StringComparer.Ordinal)) continue;

			var token = root.FindToken(diagnostic.Location.SourceSpan.Start);

			if (token.IsKind(SyntaxKind.IdentifierToken)) failing.Add(token.ValueText);
		}

		return failing;
	}

	/// <summary>
	/// The errors that mean the name still does not bind, which is the question an import answers.
	/// <para>
	/// The same three that ask for an import in the first place, and no more. An import that turns
	/// "there is no Shouted here" into "Shouted exists and wants a different receiver" did resolve the
	/// name -- what is left is a type error about code the caller wrote, which is theirs to read and
	/// not this tool claiming its own import failed.
	/// </para>
	/// </summary>
	private static readonly string[] StillFailing = ["CS0246", "CS0103", "CS1061"];
}
