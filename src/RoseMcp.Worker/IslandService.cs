using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Where a type could be cut, and what each piece would take with it.
/// <para>
/// Addressed the same two ways an outline is, a name or a path, because the question follows one:
/// a caller who has just seen a type of sixty members is holding exactly what this needs.
/// </para>
/// <para>
/// Separate from the outline rather than a switch on it, because the answer has a different shape.
/// An outline lists what is declared; this lists what would move together, and a caller wanting one
/// is not part way to wanting the other. It also costs differently -- this reads every member body
/// and resolves every name in it -- and a flag that makes a cheap call expensive is a trap.
/// </para>
/// </summary>
public static class IslandService
{
	public static async Task<IslandsResult> IslandsAsync(
		WorkspaceSnapshot snapshot,
		string? type,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var named = !string.IsNullOrWhiteSpace(type);
		var pathed = !string.IsNullOrWhiteSpace(filePath);

		if (named == pathed)
		{
			throw new ArgumentException(
				named
					? "Name a type or give a file path, not both -- they are two ways of choosing what to read."
					: "Name a type, as Namespace.Type, or give a file path.");
		}

		var notices = new List<string>(snapshot.Notices);

		var types = named
			? [await OfTypeAsync(snapshot, type!, filePath, cancellationToken)]
			: await OfFileAsync(snapshot, filePath!, cancellationToken);

		if (types.Count == 0)
		{
			notices.Add("The file declares no types.");
		}
		else if (types.All(one => one.Islands.Count == 0))
		{
			// Said rather than left to an empty array, which reads the same as a question that failed
			// to run. Finding nothing here is the common answer and a type holding together is good
			// news, so it is worth a sentence that cannot be mistaken for silence.
			notices.Add(
				types.Count == 1
					? "No islands: every member reaches the same state or the same members, so this type holds together."
					: "No islands in any of these types: their members reach the same state or the same members.");
		}

		return new IslandsResult
		{
			Revision = snapshot.Revision,
			Target = (named ? type : filePath)!,
			Types = types,
			Notices = notices,
		};
	}

	private static async Task<TypeIslands> OfTypeAsync(
		WorkspaceSnapshot snapshot,
		string type,
		string? filePath,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindTypeAsync(snapshot.Solution, type, filePath, cancellationToken);

		return await Cohesion.OfAsync(target.Symbol, snapshot.Solution, cancellationToken);
	}

	private static async Task<IReadOnlyList<TypeIslands>> OfFileAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		CancellationToken cancellationToken)
	{
		var document = SymbolLocator.RequireDocument(snapshot.Solution, filePath);

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var root = await document.GetSyntaxRootAsync(cancellationToken);

		if (model is null || root is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		var found = new List<TypeIslands>();

		// Declared order, the same as an outline's, so the two read against each other without either
		// being sorted into an order the file does not have.
		foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol) continue;

			found.Add(await Cohesion.OfAsync(symbol, snapshot.Solution, cancellationToken));
		}

		return found;
	}
}
