using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Turns the errors an edit introduced into the import that would answer them.
/// <para>
/// This is where the search is worth most, and it costs almost nothing: the compilation has just
/// been built to work out what the edit broke, so asking it what <c>Encoding</c> could be is one
/// more lookup against something already in memory. The alternative is what the write tools did
/// before -- report CS0246 and leave the caller to work out the namespace, which is a round trip
/// at exactly the moment they had been promised there would not be one.
/// </para>
/// <para>
/// The name is read out of the syntax at the diagnostic's own position rather than out of its
/// message. Compiler messages are localised and their wording is not a contract; the token under
/// the error is the same in every language.
/// </para>
/// </summary>
public static class MissingImports
{
	/// <summary>
	/// Errors that mean a name did not bind. CS0234 is deliberately absent: it fires on a qualified
	/// name whose left-hand side already resolved, so what is missing there is a reference or a
	/// spelling rather than an import.
	/// </summary>
	private static readonly string[] Unresolved = ["CS0246", "CS0103", "CS1061"];

	/// <summary>
	/// How many distinct names are looked up. An edit that introduces forty unresolved names has
	/// gone wrong in a way no import list will fix, and forty searches would make reporting that
	/// failure slower than the failure.
	/// </summary>
	private const int Looked = 5;

	/// <summary>How many files one suggestion names before it counts the rest.</summary>
	private const int FilesNamed = 5;

	/// <summary>
	/// True for a diagnostic that means a name did not bind, and so might be answered by an import.
	/// <para>
	/// Exposed because the code-fix catalogue needs the same question: these are the ids the IDE's own
	/// add-import fix would offer for, and it is not here to offer.
	/// </para>
	/// </summary>
	public static bool IsUnresolved(string id) => Unresolved.Contains(id, StringComparer.Ordinal);

	/// <summary>
	/// One line per unresolved name saying what would import it, or nothing where there is nothing
	/// useful to say.
	/// <para>
	/// A name is looked up once, and every file it failed in is kept with it: the advice depends on the
	/// file, so a name unresolved both where the tool's <c>usings</c> reaches and where it does not needs
	/// both answers, and one that failed in two files no argument reaches needs both files named.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution as the edit leaves it.</param>
	/// <param name="introduced">The errors the edit brought into being.</param>
	/// <param name="usingsReach">
	/// The files the writing tool's own <c>usings</c> argument imports into. A name unresolved in one of
	/// them is answered with that argument; anywhere else, and for a tool with no such argument, with
	/// rose_add_using alone, since an argument the tool does not take is one its caller passes for
	/// nothing.
	/// </param>
	/// <param name="cancellationToken">Cancels the lookups.</param>
	public static async Task<IReadOnlyList<string>> SuggestAsync(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<DiagnosticEntry> introduced,
		IReadOnlyCollection<string> usingsReach,
		CancellationToken cancellationToken)
	{
		var reached = usingsReach.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var names = new List<UnresolvedName>();

		foreach (var entry in introduced)
		{
			if (!IsUnresolved(entry.Id)) continue;
			if (entry.FilePath is not { Length: > 0 } path) continue;

			var unresolved = await UnresolvedAtAsync(snapshot.Solution, path, entry.Line, entry.Column, cancellationToken);
			if (unresolved is not { } found) continue;

			var known = names.Find(name => name.Name == found.Name);

			if (known is null)
			{
				if (names.Count >= Looked) continue;

				known = new UnresolvedName(found.Name, found.Use, path);
				names.Add(known);
			}

			known.FailedIn(path, reached.Contains(path));
		}

		var suggestions = new List<string>();

		foreach (var name in names)
		{
			suggestions.AddRange(await DescribeAsync(snapshot, name, cancellationToken));
		}

		return suggestions;
	}

	/// <summary>
	/// What to say about one name: the import where there is a single answer, the choice where
	/// there are several, and nothing at all where the name is simply not written yet -- silence
	/// being the honest report there, since a name that resolves to nothing is not an import
	/// problem and saying it might be would send the caller looking in the wrong place.
	/// <para>
	/// The single answer names the <c>usings</c> argument for the files the tool's own argument
	/// reaches, and rose_add_using with each other file for the rest, which is the one way to the
	/// import that every caller has. A file is named by its whole path, because that is what
	/// rose_add_using's filePath is matched against, and two files can share a name.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<string>> DescribeAsync(
		WorkspaceSnapshot snapshot,
		UnresolvedName name,
		CancellationToken cancellationToken)
	{
		var resolution = await NameResolver.ResolveAsync(
			snapshot,
			new ResolveNameRequest { Name = name.Name, FilePath = name.FirstPath, Use = name.Use },
			cancellationToken);

		var usable = resolution.Candidates
			.Where(candidate => candidate.AlreadyInScope is null && candidate.Caveat is null)
			.ToArray();

		if (resolution.Import is { } single)
		{
			// Named where one symbol answers for the namespace, and left unnamed where several do --
			// three overloads of one extension method are still one import, and listing them would
			// read as a choice the caller has to make when there is none.
			var what = usable is [{ } only] ? only.Symbol : $"in {single}";
			var said = new List<string>();

			if (name.Reached) said.Add($"{name.Name} is {what}: pass usings: [\"{single}\"], or call rose_add_using.");

			if (name.Elsewhere.Count > 0)
			{
				var lead = name.Reached ? $"{name.Name} is also unresolved where usings does not reach" : $"{name.Name} is {what}";

				said.Add($"{lead}: call rose_add_using with namespaces: [\"{single}\"] {On(name.Elsewhere)}.");
			}

			return said;
		}

		var spaces = usable
			.Select(candidate => candidate.Namespace)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		if (spaces.Length > 1)
		{
			return
			[
				$"{name.Name} is in {spaces.Length} namespaces ({string.Join(", ", spaces)}); rose_resolve_name "
					+ "describes them, and importing the wrong one compiles.",
			];
		}

		// Everything found carries a reason it would not help, and the reason is the useful part: a
		// nested type or an unreferenced project is a different fix from an import.
		if (resolution.Candidates is [{ Caveat: { } caveat } sole]) return [$"{name.Name} is {sole.Symbol}, {caveat}."];

		return [];
	}

	/// <summary>
	/// The files a rose_add_using call is wanted on, one call per file since it takes one, and a count
	/// past <see cref="FilesNamed"/> rather than a list nobody reads to the end of.
	/// </summary>
	private static string On(IReadOnlyList<string> files)
	{
		if (files.Count == 1) return $"on {files[0]}";

		var named = files.Take(FilesNamed).ToList();
		var rest = files.Count - named.Count;

		if (rest > 0) named.Add($"{rest} more file(s) the introduced errors name");

		return $"once for each of {string.Join(", ", named.Take(named.Count - 1))} and {named[^1]}";
	}

	/// <summary>
	/// One name that did not bind, with where: whether any of it is somewhere the tool's own
	/// <c>usings</c> reaches, and every other file, in the order the errors came.
	/// </summary>
	private sealed class UnresolvedName(string name, NameUse use, string firstPath)
	{
		private readonly List<string> _elsewhere = [];

		public string Name { get; } = name;

		public NameUse Use { get; } = use;

		/// <summary>Where the name is resolved from, which decides what is reachable.</summary>
		public string FirstPath { get; } = firstPath;

		public bool Reached { get; private set; }

		public IReadOnlyList<string> Elsewhere => _elsewhere;

		public void FailedIn(string path, bool reached)
		{
			if (reached)
			{
				Reached = true;

				return;
			}

			if (!_elsewhere.Contains(path, StringComparer.OrdinalIgnoreCase)) _elsewhere.Add(path);
		}
	}

	/// <summary>
	/// The identifier the diagnostic is pointing at. Null where the position cannot be read, which
	/// is what happens for a diagnostic inside generated code: its file exists only in the
	/// compilation, so there is no document to find.
	/// </summary>
	public static async Task<string?> NameAtAsync(
		Solution solution,
		string filePath,
		int line,
		int column,
		CancellationToken cancellationToken) =>
		(await UnresolvedAtAsync(solution, filePath, line, column, cancellationToken))?.Name;

	/// <summary>
	/// The identifier the diagnostic is pointing at and how the code uses it, which together are the
	/// question an import answers: not only what is called that, but what kind of thing could be.
	/// </summary>
	public static async Task<(string Name, NameUse Use)?> UnresolvedAtAsync(
		Solution solution,
		string filePath,
		int line,
		int column,
		CancellationToken cancellationToken)
	{
		var document = SymbolLocator.FindDocument(solution, filePath);
		if (document is null) return null;

		var text = await document.GetTextAsync(cancellationToken);
		if (line < 1 || line > text.Lines.Count) return null;

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		if (root is null) return null;

		var textLine = text.Lines[line - 1];
		var position = textLine.Start + Math.Clamp(column - 1, 0, textLine.Span.Length);
		var token = root.FindToken(position);

		return token.IsKind(SyntaxKind.IdentifierToken) ? (token.ValueText, NameUses.Of(token)) : null;
	}
}
