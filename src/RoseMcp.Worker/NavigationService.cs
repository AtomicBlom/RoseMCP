using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Semantic navigation: what a symbol is, where it is used, and finding it by name.</summary>
public static class NavigationService
{
	public static async Task<SymbolInfoResult> DescribeAsync(
		WorkspaceSnapshot snapshot,
		SymbolTarget request,
		CancellationToken cancellationToken,
		bool includeSource = false)
	{
		// The one read that can say something true about a symbol it cannot edit, so it is the one that
		// looks in metadata when nothing in source carries the name.
		var symbol = await request.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);

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

			// A symbol from metadata has no source locations, which is also why it cannot be renamed. The
			// assembly it came from is named in its place, since that is the only place it can be looked at.
			IsFromSource = declarations.Count > 0,
			ContainingAssembly = declarations.Count > 0 ? null : symbol.ContainingAssembly?.Identity.Name,

			Source = includeSource ? await SourceOfAsync(symbol, cancellationToken) : [],
		};
	}

	/// <summary>
	/// Every reference to a symbol, with three ways to ask for less of it.
	/// <para>
	/// A widely used member answers at a size nothing can read: one member of a test fixture came back
	/// at 63 KB, and <paramref name="maxResults"/> is no answer to it, because it drops references and
	/// the previews on the ones it keeps are most of the payload. So the payload is separable from the
	/// list -- how widely a symbol is used, which projects use it, and where exactly, are three
	/// questions of very different sizes and only the last of them needs a line of source per hit.
	/// </para>
	/// </summary>
	/// <param name="snapshot">The solution to search.</param>
	/// <param name="target">The symbol, named or pointed at.</param>
	/// <param name="maxResults">How many references to return.</param>
	/// <param name="cancellationToken">Cancels the search.</param>
	/// <param name="definitionsOnly">
	/// Return where the symbol is declared and how many uses there are, without listing them. The count
	/// is the whole answer to "is this used at all" and to "is this safe to change", at a fraction of
	/// the size.
	/// </param>
	/// <param name="project">
	/// Only references compiled by this project. Named rather than filtered by the caller afterwards,
	/// because the truncation happens here: a symbol used five hundred times in tests and twice in the
	/// product answers with the two only if the narrowing reaches the search.
	/// </param>
	/// <param name="includePreviews">
	/// Whether each location carries its line of source. The member each reference sits inside is
	/// reported either way, and that is what turns a flat list into "used by these six methods".
	/// </param>
	/// <exception cref="ArgumentException">The solution has no project of that name.</exception>
	public static async Task<ReferencesResult> FindReferencesAsync(
		WorkspaceSnapshot snapshot,
		SymbolTarget target,
		int maxResults,
		CancellationToken cancellationToken,
		bool definitionsOnly = false,
		string? project = null,
		bool includePreviews = true)
	{
		GuardProject(snapshot, project);

		// Metadata included: who calls ILogger.LogInformation in this solution is a question about this
		// solution's source, and refusing it because nothing here declares the member answers a narrower
		// question than the one asked. The definitions come back empty, since a metadata symbol has no
		// source location, and the references are the answer.
		var symbol = await target.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);
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
			.Where(location => InProject(location, project))
			.OrderBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(location => location.Line)
			.ToArray();

		// Truncated says the list is not all of them, which is as true of asking for none as of asking
		// for two hundred. The count beside it is the real one either way.
		var listed = definitionsOnly ? [] : ordered.Length > maxResults ? ordered[..maxResults] : ordered;

		return new ReferencesResult
		{
			Revision = snapshot.Revision,
			Symbol = symbol.ToDisplayString(SymbolSignature.Format),
			Definitions = [.. definitions.Select(location => Previewed(location, includePreviews))],
			References = [.. listed.Select(location => Previewed(location, includePreviews))],
			TotalCount = ordered.Length,
			Truncated = listed.Length < ordered.Length,
		};
	}

	/// <summary>
	/// Refuses a project name the solution does not carry. Filtering silently on a name nothing
	/// matches returns an empty list, which reads exactly like a symbol nobody uses -- the shape of
	/// wrong answer worth the most trouble to avoid, since it invites a deletion.
	/// </summary>
	private static void GuardProject(WorkspaceSnapshot snapshot, string? project)
	{
		if (project is not { Length: > 0 }) return;

		var names = snapshot.Solution.Projects.Select(candidate => candidate.Name).ToArray();

		if (names.Any(name => string.Equals(name, project, StringComparison.OrdinalIgnoreCase))) return;

		throw new ArgumentException(
			$"No project in this solution is called '{project}'. It has {string.Join(", ", names.Order(StringComparer.Ordinal))}.");
	}

	/// <summary>Whether a location belongs to the project the caller narrowed to, or to any if none.</summary>
	private static bool InProject(SourceLocation location, string? project) =>
		project is not { Length: > 0 }
			|| string.Equals(location.Project, project, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The location with or without its line of source. Dropping the preview is most of the size of a
	/// large answer, and it costs the caller nothing they cannot get back by asking about one file.
	/// </summary>
	private static SourceLocation Previewed(SourceLocation location, bool includePreviews) =>
		includePreviews ? location : location with { Preview = null };

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
		SymbolTarget target,
		int maxResults,
		CancellationToken cancellationToken)
	{
		// Metadata included, and this is where it earns most: what in this solution implements
		// IDisposable or derives from Exception is a question about source, asked of a type no project
		// here declares, and it is the ordinary shape of the question rather than an edge of it.
		var symbol = await target.ResolveAsync(snapshot, cancellationToken, includeMetadata: true);
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

		// A type's bases are the same question one level up, and were previously answered only for a
		// member -- so asking what a class derives from returned nothing at all.
		if (symbol is INamedTypeSymbol named)
		{
			if (named.BaseType is { SpecialType: not SpecialType.System_Object } super) bases.Add(super);

			bases.AddRange(named.Interfaces);

			return [.. bases.Distinct(SymbolEqualityComparer.Default)];
		}

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

	/// <summary>
	/// The text of every declaration of a symbol, straight out of the tree it was parsed from.
	/// <para>
	/// The full span rather than the span alone, so the documentation comment and the attributes come
	/// with the member -- they are what a reader wanted the source for as often as the code is.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<string>> SourceOfAsync(ISymbol symbol, CancellationToken cancellationToken)
	{
		var written = new List<string>();

		foreach (var reference in symbol.DeclaringSyntaxReferences)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var node = await reference.GetSyntaxAsync(cancellationToken);

			written.Add(node.ToFullString().Trim());
		}

		return written;
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
