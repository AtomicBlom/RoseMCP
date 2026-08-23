using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Semantic navigation: what a symbol is, where it is used, and finding it by name.</summary>
public static class NavigationService
{
	private static readonly SymbolDisplayFormat SignatureFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		memberOptions: SymbolDisplayMemberOptions.IncludeParameters
			| SymbolDisplayMemberOptions.IncludeType
			| SymbolDisplayMemberOptions.IncludeContainingType,
		parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	public static async Task<SymbolInfoResult> DescribeAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		int line,
		int column,
		CancellationToken cancellationToken)
	{
		var (symbol, _) = await SymbolLocator.ResolveAsync(snapshot.Solution, filePath, line, column, cancellationToken);

		var declarations = new List<SourceLocation>();
		foreach (var location in symbol.Locations.Where(location => location.IsInSource))
		{
			declarations.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken));
		}

		var documentation = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);

		return new SymbolInfoResult
		{
			Revision = snapshot.Revision,
			Name = symbol.Name,
			Kind = symbol.Kind.ToString(),
			Signature = symbol.ToDisplayString(SignatureFormat),
			Accessibility = symbol.DeclaredAccessibility.ToString(),
			ContainingType = symbol.ContainingType?.ToDisplayString(SignatureFormat),
			Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
				? symbol.ContainingNamespace.ToDisplayString()
				: null,
			Documentation = string.IsNullOrWhiteSpace(documentation) ? null : documentation,
			Declarations = declarations,

			// A symbol from metadata has no source locations, which is also why it cannot be renamed.
			IsFromSource = declarations.Count > 0,
		};
	}

	public static async Task<ReferencesResult> FindReferencesAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		int line,
		int column,
		int maxResults,
		CancellationToken cancellationToken)
	{
		var (symbol, _) = await SymbolLocator.ResolveAsync(snapshot.Solution, filePath, line, column, cancellationToken);
		var found = await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken);

		var definitions = new List<SourceLocation>();
		var references = new List<SourceLocation>();

		foreach (var reference in found)
		{
			foreach (var location in reference.Definition.Locations.Where(location => location.IsInSource))
			{
				definitions.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken));
			}

			foreach (var location in reference.Locations)
			{
				if (location.Location.IsInSource)
				{
					references.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location.Location, cancellationToken));
				}
			}
		}

		var ordered = references
			.OrderBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(location => location.Line)
			.ToArray();

		var truncated = ordered.Length > maxResults;

		return new ReferencesResult
		{
			Revision = snapshot.Revision,
			Symbol = symbol.ToDisplayString(SignatureFormat),
			Definitions = definitions,
			References = truncated ? ordered[..maxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
		};
	}

	public static async Task<SymbolSearchResult> SearchAsync(
		WorkspaceSnapshot snapshot,
		string query,
		int maxResults,
		CancellationToken cancellationToken)
	{
		var matches = new List<SymbolMatch>();

		foreach (var project in snapshot.Solution.Projects)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Pattern search understands the abbreviations people actually type -- "SLoader" finds
			// SolutionLoader -- which plain substring matching does not.
			var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query, cancellationToken);

			foreach (var symbol in found)
			{
				var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);

				matches.Add(new SymbolMatch
				{
					Name = symbol.Name,
					Kind = symbol.Kind.ToString(),
					Signature = symbol.ToDisplayString(SignatureFormat),
					Project = project.Name,
					Location = location is null
						? null
						: await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken),
				});
			}
		}

		var ordered = matches
			.OrderBy(match => match.Name.Length)
			.ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var truncated = ordered.Length > maxResults;

		return new SymbolSearchResult
		{
			Revision = snapshot.Revision,
			Matches = truncated ? ordered[..maxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
		};
	}
}
