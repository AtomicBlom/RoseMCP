using Microsoft.CodeAnalysis;
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
		var reported = new List<string>();
		var ambiguous = new List<string>();
		var unresolved = new List<string>();
		var asked = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in introduced)
		{
			if (!MissingImports.IsUnresolved(entry.Id)) continue;
			if (entry.FilePath is not { Length: > 0 } path) continue;
			if (!SamePath(path, filePath)) continue;
			if (asked.Count >= limit) break;

			var name = await MissingImports.NameAtAsync(snapshot.Solution, path, entry.Line, entry.Column, cancellationToken);
			if (name is null || !asked.Add(name)) continue;

			var resolution = await NameResolver.ResolveAsync(
				snapshot, new ResolveNameRequest { Name = name, FilePath = path }, cancellationToken);

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
				reported.Add($"{name}: imported {single}.");

				continue;
			}

			unresolved.Add(resolution.Candidates is [{ Caveat: { } caveat } sole]
				? $"{name} is {sole.Symbol}, {caveat}."
				: $"{name} resolves to nothing in scope, and no import would fix it.");
		}

		return new Imports(add, reported, ambiguous, unresolved);
	}

	/// <summary>
	/// The solution with those namespaces imported into one document, in whatever order and grouping
	/// the file itself uses.
	/// </summary>
	/// <param name="solution">The solution as the edit leaves it.</param>
	/// <param name="id">The document to import into.</param>
	/// <param name="namespaces">Namespaces to ensure, each already known to have one answer.</param>
	/// <param name="cancellationToken">Cancels the scope lookups.</param>
	public static async Task<Solution> ApplyAsync(
		Solution solution,
		DocumentId id,
		IReadOnlyList<string> namespaces,
		CancellationToken cancellationToken)
	{
		if (namespaces.Count == 0 || solution.GetDocument(id) is not { } document) return solution;

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);

		if (model is null || tree is null) return solution;
		if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root) return solution;

		var rules = Whitespace.RulesFor(document.Project, tree, text);
		var style = UsingStyle.For(document.Project, tree, root, rules.LineEnding);

		var insertion = UsingDirectives.Ensure(root, model, namespaces, style, cancellationToken);

		return insertion.Added.Count == 0
			? solution
			: solution.WithDocumentSyntaxRoot(id, insertion.Root);
	}

	/// <summary>
	/// The three answers, kept apart on purpose. Folding ambiguous into unresolved would tell a
	/// caller a type does not exist when the problem is that it exists twice, which sends them off
	/// to write one.
	/// </summary>
	/// <param name="Namespaces">Namespaces to import, each with exactly one answer behind it.</param>
	/// <param name="Added">One line per import, naming which unresolved name wanted it.</param>
	/// <param name="Ambiguous">Names with more than one candidate; nothing was imported for these.</param>
	/// <param name="Unresolved">Names no import would fix, with why not.</param>
	public sealed record Imports(
		IReadOnlyList<string> Namespaces,
		IReadOnlyList<string> Added,
		IReadOnlyList<string> Ambiguous,
		IReadOnlyList<string> Unresolved)
	{
		public static readonly Imports None = new([], [], [], []);

		public bool AnythingToAdd => Namespaces.Count > 0;
	}

	private static bool SamePath(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
