using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Semantic navigation: what a symbol is, where it is used, and finding it by name.</summary>
public static class NavigationService
{
	public static async Task<SymbolInfoResult> DescribeAsync(
		WorkspaceSnapshot snapshot,
		SymbolInfoRequest request,
		CancellationToken cancellationToken)
	{
		var symbol = await ResolveAsync(snapshot, request, cancellationToken);

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
			Signature = symbol.ToDisplayString(SymbolSignature.Format),
			Accessibility = symbol.DeclaredAccessibility.ToString(),
			ContainingType = symbol.ContainingType?.ToDisplayString(SymbolSignature.Format),
			Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
				? symbol.ContainingNamespace.ToDisplayString()
				: null,
			Documentation = string.IsNullOrWhiteSpace(documentation) ? null : documentation,
			Declarations = declarations,
			DeclarationSpans = await SymbolLocator.SpansOfAsync(symbol, cancellationToken),
			BaseDefinitions = await DescribeAllAsync(snapshot, BaseDefinitions(symbol), cancellationToken),

			// A symbol from metadata has no source locations, which is also why it cannot be renamed.
			IsFromSource = declarations.Count > 0,
		};
	}

	/// <summary>
	/// The symbol the request names, however it named it. A request that says neither is an error
	/// rather than a default: guessing which of the two was meant would answer about some other
	/// symbol entirely, and answering confidently about the wrong symbol is the failure worth the
	/// most trouble to avoid.
	/// </summary>
	private static async Task<ISymbol> ResolveAsync(
		WorkspaceSnapshot snapshot,
		SymbolInfoRequest request,
		CancellationToken cancellationToken)
	{
		if (request.IsByName)
		{
			var target = await DeclarationLocator.FindSymbolAsync(
				snapshot.Solution, request.Symbol!, request.FilePath, cancellationToken);

			return target.Symbol;
		}

		if (!request.IsByPosition)
		{
			throw new ArgumentException(
				"Name the symbol, as Namespace.Type.Member, or give filePath with line and column. "
					+ "A name needs no position and does not go stale when the file is edited.");
		}

		var (symbol, _) = await SymbolLocator.ResolveAsync(
			snapshot.Solution, request.FilePath!, request.Line!.Value, request.Column!.Value, cancellationToken);

		return symbol;
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
			Symbol = symbol.ToDisplayString(SymbolSignature.Format),
			Definitions = definitions,
			References = truncated ? ordered[..maxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
		};
	}

	/// <summary>
	/// What implements, overrides, or derives from the symbol at a position.
	/// <para>
	/// One tool rather than three, because the question a caller has is the same one -- "who else is
	/// involved in this" -- and which Roslyn call answers it is decided by what the symbol turns out
	/// to be. Answering the wrong question silently would be worse than answering none, so which one
	/// was answered is reported back.
	/// </para>
	/// </summary>
	public static async Task<ImplementationsResult> FindImplementationsAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		int line,
		int column,
		int maxResults,
		CancellationToken cancellationToken)
	{
		var (symbol, _) = await SymbolLocator.ResolveAsync(snapshot.Solution, filePath, line, column, cancellationToken);
		var solution = snapshot.Solution;
		var found = new List<ISymbol>();
		string relationship;

		if (symbol is INamedTypeSymbol type)
		{
			if (type.TypeKind == TypeKind.Interface)
			{
				relationship = "types implementing this interface, and interfaces extending it";
				found.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cancellationToken));
				found.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, cancellationToken: cancellationToken));
			}
			else
			{
				relationship = "types derived from this one";
				found.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken));
			}
		}
		else
		{
			// A member can be both overridden and an interface implementation, and a caller asking
			// about one usually wants the other too.
			relationship = "members overriding or implementing this one";
			found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken));
			found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
		}

		var matches = await DescribeAllAsync(
			snapshot,
			found.Distinct(SymbolEqualityComparer.Default).ToArray(),
			cancellationToken);

		var ordered = matches
			.OrderBy(match => match.Signature, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var truncated = ordered.Length > maxResults;

		return new ImplementationsResult
		{
			Revision = snapshot.Revision,
			Symbol = symbol.ToDisplayString(SymbolSignature.Format),
			Relationship = relationship,
			Matches = truncated ? ordered[..maxResults] : ordered,
			TotalCount = ordered.Length,
			Truncated = truncated,
		};
	}

	/// <summary>What a member overrides and what it implements, which is the same list to a caller.</summary>
	private static IReadOnlyList<ISymbol> BaseDefinitions(ISymbol symbol)
	{
		var bases = new List<ISymbol>();

		var overridden = symbol switch
		{
			IMethodSymbol method => method.OverriddenMethod,
			IPropertySymbol property => (ISymbol?)property.OverriddenProperty,
			IEventSymbol @event => @event.OverriddenEvent,
			_ => null,
		};

		if (overridden is not null) bases.Add(overridden);

		if (symbol.ContainingType is { } containing)
		{
			bases.AddRange(containing.AllInterfaces
				.SelectMany(@interface => @interface.GetMembers())
				.Where(member => SymbolEqualityComparer.Default.Equals(
					containing.FindImplementationForInterfaceMember(member), symbol)));
		}

		return [.. bases.Distinct(SymbolEqualityComparer.Default)];
	}

	private static async Task<IReadOnlyList<SymbolMatch>> DescribeAllAsync(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<ISymbol> symbols,
		CancellationToken cancellationToken)
	{
		var matches = new List<SymbolMatch>();

		foreach (var symbol in symbols)
		{
			var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
			var document = location?.SourceTree is { } tree ? snapshot.Solution.GetDocument(tree) : null;

			matches.Add(new SymbolMatch
			{
				Name = symbol.Name,
				Kind = symbol.Kind.ToString(),
				Signature = symbol.ToDisplayString(SymbolSignature.Format),

				// Metadata symbols belong to no project in the solution, and saying so is more use
				// than an empty string that reads like a bug.
				Project = document?.Project.Name ?? symbol.ContainingAssembly?.Name ?? "(metadata)",
				Location = location is null
					? null
					: await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken),
			});
		}

		return matches;
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
					Signature = symbol.ToDisplayString(SymbolSignature.Format),
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
