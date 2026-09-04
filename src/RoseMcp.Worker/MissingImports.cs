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
	/// </summary>
	/// <param name="snapshot">The solution as the edit leaves it.</param>
	/// <param name="introduced">The errors the edit brought into being.</param>
	/// <param name="cancellationToken">Cancels the lookups.</param>
	public static async Task<IReadOnlyList<string>> SuggestAsync(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<DiagnosticEntry> introduced,
		CancellationToken cancellationToken)
	{
		var suggestions = new List<string>();
		var asked = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in introduced)
		{
			if (!IsUnresolved(entry.Id)) continue;
			if (entry.FilePath is not { Length: > 0 } path) continue;
			if (asked.Count >= Looked) break;

			var name = await NameAtAsync(snapshot.Solution, path, entry.Line, entry.Column, cancellationToken);
			if (name is null || !asked.Add(name)) continue;

			if (await DescribeAsync(snapshot, name, path, cancellationToken) is { } suggestion)
			{
				suggestions.Add(suggestion);
			}
		}

		return suggestions;
	}

	/// <summary>
	/// What to say about one name: the import where there is a single answer, the choice where
	/// there are several, and nothing at all where the name is simply not written yet -- silence
	/// being the honest report there, since a name that resolves to nothing is not an import
	/// problem and saying it might be would send the caller looking in the wrong place.
	/// </summary>
	private static async Task<string?> DescribeAsync(
		WorkspaceSnapshot snapshot,
		string name,
		string filePath,
		CancellationToken cancellationToken)
	{
		var resolution = await NameResolver.ResolveAsync(
			snapshot,
			new ResolveNameRequest { Name = name, FilePath = filePath },
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

			return $"{name} is {what}: pass usings: [\"{single}\"], or call rose_add_using.";
		}

		var spaces = usable
			.Select(candidate => candidate.Namespace)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		if (spaces.Length > 1)
		{
			return $"{name} is in {spaces.Length} namespaces ({string.Join(", ", spaces)}); rose_resolve_name "
				+ "describes them, and importing the wrong one compiles.";
		}

		// Everything found carries a reason it would not help, and the reason is the useful part: a
		// nested type or an unreferenced project is a different fix from an import.
		if (resolution.Candidates is [{ Caveat: { } caveat } sole]) return $"{name} is {sole.Symbol}, {caveat}.";

		return null;
	}

	/// <summary>
	/// The identifier the diagnostic is pointing at. Null where the position cannot be read, which
	/// is what happens for a diagnostic inside generated code: its file exists only in the
	/// compilation, so there is no document to find.
	/// </summary>
	private static async Task<string?> NameAtAsync(
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

		return token.IsKind(SyntaxKind.IdentifierToken) ? token.ValueText : null;
	}
}
