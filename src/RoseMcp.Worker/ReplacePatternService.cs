using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;
using RoseMcp.Patterns;

namespace RoseMcp.Worker;

/// <summary>
/// A structural rewrite over the loaded solution: a rule catalog bound in each project in scope, every
/// document scanned, and the result told as a summary.
/// <para>
/// Each project binds the catalog in its own compilation, because a rule means what its names mean
/// there: one project's <c>Assert</c> is not necessarily another's, and a project that does not
/// reference what a rule is about simply has nothing for that rule to match. A rule that binds in no
/// project in scope is the caller's mistake rather than a fact about a project, and is refused.
/// </para>
/// </summary>
public static class ReplacePatternService
{
	/// <summary>Matches <paramref name="request"/>'s rules across its scope and reports what they would do.</summary>
	public static async Task<MutationResult<PatternRewriteResult>> ReplaceAsync(
		WorkspaceSnapshot snapshot,
		ReplacePatternRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		snapshot.RefuseIfMoved(request.ExpectedRevision);

		if (request.Apply)
		{
			throw new ArgumentException("rose_replace_pattern only previews until its replacements are built: pass apply=false.");
		}

		var catalog = RuleCatalog.Parse([.. request.Rules.Select(rule => new RuleText(rule.Find, rule.Replace))], request.Usings);
		var scope = Scope(snapshot.Solution, request.FilePaths);

		var sites = new List<SiteRecord>();
		var misses = new List<MissRecord>();
		var boundTo = new Dictionary<int, SortedSet<string>>();
		var unbound = new Dictionary<int, string>();
		var seen = new HashSet<(string Path, int Start)>();

		for (var index = 0; index < scope.Count; index++)
		{
			var (project, documents) = scope[index];

			progress?.Report($"Matching in {project.Name} ({index + 1}/{scope.Count})", 90.0 * index / scope.Count);

			if (await project.GetCompilationAsync(cancellationToken) is not CSharpCompilation compilation) continue;

			var bound = catalog.Bind(compilation, cancellationToken);

			foreach (var (number, reason) in bound.Unbound) unbound.TryAdd(number, reason);

			foreach (var rule in bound.Bound)
			{
				if (!boundTo.TryGetValue(rule.Rule.Number, out var addresses))
				{
					boundTo[rule.Rule.Number] = addresses = new SortedSet<string>(StringComparer.Ordinal);
				}

				foreach (var method in rule.Methods) addresses.Add(Address(method));
			}

			if (bound.Bound.Count == 0) continue;

			foreach (var document in documents)
			{
				if (await document.GetSyntaxTreeAsync(cancellationToken) is not { } tree || document.FilePath is not { } path) continue;

				var text = await tree.GetTextAsync(cancellationToken);
				var scan = bound.Scan(compilation.GetSemanticModel(tree), cancellationToken);

				// A file compiled by two projects -- a linked file, a multi-targeted project -- is met once
				// per project, and one site is still one site.
				foreach (var site in scan.Sites.Where(site => seen.Add((path, site.Node.SpanStart))))
				{
					sites.Add(new SiteRecord
					{
						Rule = site.Rule.Number,
						Location = Locate(site.Node, path, text),
						Before = site.Node.ToString(),
						AlsoMatched = site.AlsoMatched,
					});
				}

				foreach (var miss in scan.Unmatched.Where(miss => seen.Add((path, miss.Node.SpanStart))))
				{
					misses.Add(new MissRecord(Address(miss.Method), Locate(miss.Node, path, text), miss.Binds));
				}
			}
		}

		if (boundTo.Count == 0) throw BindsNowhere(catalog, unbound);

		var summary = PatternReport.Build(
			catalog.Rules.Count,
			boundTo.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value]),
			sites,
			misses,
			preview: !request.Apply);

		var notices = new List<string>(snapshot.Notices);

		foreach (var rule in catalog.Rules.Where(rule => !boundTo.ContainsKey(rule.Number)))
		{
			notices.Add($"Rule {rule.Number} binds in no project in scope. {unbound[rule.Number]}.");
		}

		notices.AddRange(summary.Notices);
		notices.Add("Preview only; nothing was written to disk.");

		var result = new PatternRewriteResult
		{
			Revision = snapshot.Revision,
			Applied = false,
			SitesMatched = summary.Matched,
			SitesRewritten = summary.Rewritten,
			SitesSkipped = summary.SkippedCount,
			SitesUnmatched = summary.UnmatchedCount,
			Rules = summary.Rules,
			Skipped = summary.Skipped,
			Unmatched = summary.Unmatched,
			Files = summary.Files,
			FileCount = summary.FileCount,
			Diff = string.Empty,
			Notices = notices,
		};

		return new MutationResult<PatternRewriteResult>(result, null);
	}

	/// <summary>
	/// The C# documents in scope, by project: every one under a path the caller named, or every one in
	/// the solution when they named none. A path that names nothing is refused, since a rewrite that
	/// silently covered nothing would read as one that found nothing to do.
	/// </summary>
	private static IReadOnlyList<(Project Project, IReadOnlyList<Document> Documents)> Scope(Solution solution, IReadOnlyList<string> filePaths)
	{
		var wanted = filePaths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToList();
		var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var scope = new List<(Project, IReadOnlyList<Document>)>();

		foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
		{
			var documents = project.Documents
				.Where(document => document.FilePath is { } path && InScope(wanted, path, reached))
				.ToList();

			if (documents.Count > 0) scope.Add((project, documents));
		}

		var missing = wanted.Where(path => !reached.Contains(path)).ToList();

		if (missing.Count > 0)
		{
			throw new ArgumentException(
				$"No C# file in this solution is at or under {string.Join(", ", missing.Select(path => $"'{path}'"))}.");
		}

		return scope;
	}

	/// <summary>Whether <paramref name="path"/> is one of <paramref name="wanted"/> or under one, noting which it reached.</summary>
	private static bool InScope(IReadOnlyList<string> wanted, string path, HashSet<string> reached)
	{
		if (wanted.Count == 0) return true;

		var inScope = false;

		foreach (var candidate in wanted)
		{
			var covers = string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)
				|| path.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

			if (!covers) continue;

			reached.Add(candidate);
			inScope = true;
		}

		return inScope;
	}

	/// <summary>A method as an address a caller can pass back to another tool.</summary>
	private static string Address(IMethodSymbol method) => SymbolAddress.Of(method) ?? method.ToDisplayString();

	/// <summary>Where a node starts, with its line of source.</summary>
	private static SourceLocation Locate(SyntaxNode node, string path, SourceText text)
	{
		var position = text.Lines.GetLinePosition(node.SpanStart);

		return new SourceLocation
		{
			FilePath = path,
			Line = position.Line + 1,
			Column = position.Character + 1,
			Preview = text.Lines[position.Line].ToString().Trim(),
		};
	}

	/// <summary>The refusal for a catalog none of whose rules binds anywhere in scope, with each rule's reason.</summary>
	private static ArgumentException BindsNowhere(RuleCatalog catalog, IReadOnlyDictionary<int, string> unbound)
	{
		var reasons = catalog.Rules
			.Select(rule => unbound.TryGetValue(rule.Number, out var reason) ? reason : $"Rule {rule.Number} was not bound")
			.Take(3);

		return new ArgumentException(
			"No rule binds in any project in scope, so there is nothing to match. " + string.Join(". ", reasons)
			+ ". A name in a find resolves as it would in the project: is its namespace in usings, or the project's own usings?");
	}
}
