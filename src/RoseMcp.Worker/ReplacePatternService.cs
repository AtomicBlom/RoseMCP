using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;
using RoseMcp.Patterns;

namespace RoseMcp.Worker;

/// <summary>
/// A structural rewrite over the loaded solution: a rule catalog bound in each project in scope, every
/// document scanned and rewritten, and the result told as a summary.
/// <para>
/// Each project binds the catalog in its own compilation, because a rule means what its names mean
/// there: one project's <c>Assert</c> is not necessarily another's, and a project that does not
/// reference what a rule is about simply has nothing for that rule to match. A rule that binds in no
/// project in scope is the caller's mistake rather than a fact about a project, and is refused.
/// </para>
/// <para>
/// A preview does everything an apply does except write: the replacements are built, compiled and put
/// back where they would not compile, so what a preview lists as skipped is what an apply would skip.
/// </para>
/// </summary>
public static class ReplacePatternService
{
	/// <summary>The longest diff a result carries; beyond it, a caller narrows the scope to read one.</summary>
	internal const int DiffCeiling = 16_000;

	/// <summary>Rewrites what <paramref name="request"/>'s rules match across its scope, or previews it.</summary>
	public static async Task<MutationResult<PatternRewriteResult>> ReplaceAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		ReplacePatternRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var edit = EditPipeline.Begin(snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		var catalog = RuleCatalog.Parse([.. request.Rules.Select(rule => new RuleText(rule.Find, rule.Replace))], request.Usings);
		var scope = Scope(snapshot.Solution, request.FilePaths);

		var sites = new List<SiteRecord>();
		var misses = new List<MissRecord>();
		var boundTo = new Dictionary<int, SortedSet<string>>();
		var unbound = new Dictionary<int, string>();
		var seen = new HashSet<(string Path, int Start)>();
		var solution = snapshot.Solution;
		var asked = Asked.Nothing;
		var changedProjects = new HashSet<string>(StringComparer.Ordinal);
		var subsumed = 0;

		for (var index = 0; index < scope.Count; index++)
		{
			var (project, documents) = scope[index];

			progress?.Report($"Rewriting in {project.Name} ({index + 1}/{scope.Count})", 80.0 * index / scope.Count);

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

			var isTest = TestProjects.IsTest(project);
			var matched = new List<DocumentSites>();
			var documentOf = new Dictionary<SyntaxTree, Document>();

			foreach (var document in documents)
			{
				if (await document.GetSyntaxTreeAsync(cancellationToken) is not { } tree || document.FilePath is not { } path) continue;

				var text = await tree.GetTextAsync(cancellationToken);
				var scan = bound.Scan(compilation.GetSemanticModel(tree), cancellationToken);

				// A file compiled by two projects -- a linked file, a multi-targeted project -- is met once
				// per project, and one site is still one site.
				var fresh = scan.Sites.Where(site => seen.Add((path, site.Node.SpanStart))).ToList();

				foreach (var miss in scan.Unmatched.Where(miss => seen.Add((path, miss.Node.SpanStart))))
				{
					misses.Add(new MissRecord(Address(miss.Method), Locate(miss.Node, path, text, isTest), miss.Binds));
				}

				if (fresh.Count == 0) continue;

				matched.Add(new DocumentSites(tree, fresh));
				documentOf[tree] = document;
			}

			if (matched.Count == 0) continue;

			var rewrites = RewriteEngine.Run(compilation, matched, Importer(project, compilation, bound.Usings, cancellationToken), cancellationToken);

			foreach (var rewrite in rewrites)
			{
				var document = documentOf[rewrite.Original];
				var text = await rewrite.Original.GetTextAsync(cancellationToken);

				foreach (var outcome in rewrite.Sites)
				{
					if (outcome.State == SiteState.Subsumed) subsumed++;

					sites.Add(new SiteRecord
					{
						Rule = outcome.Site.Rule.Number,
						Location = Locate(outcome.Site.Node, document.FilePath!, text, isTest),
						Before = outcome.Site.Node.ToString(),
						After = outcome.After,
						AlsoMatched = outcome.Site.AlsoMatched,
						SkippedId = outcome.State == SiteState.Skipped ? outcome.DiagnosticId : null,
						SkippedReason = outcome.State == SiteState.Skipped ? outcome.Reason : null,
					});
				}

				if (rewrite.Root is null) continue;

				var original = (CompilationUnitSyntax)await rewrite.Original.GetRootAsync(cancellationToken);
				var written = rewrite.Sites.Where(outcome => outcome.State == SiteState.Rewritten).Select(outcome => outcome.Site.Node.FullSpan);

				solution = await FormattedAsync(solution, document.Id, rewrite.Root, cancellationToken);
				asked = asked.And(document, written.Append(UsingDirectives.Region(original)));
				changedProjects.Add(project.Name);
			}
		}

		if (boundTo.Count == 0) throw BindsNowhere(catalog, unbound);

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 85);

		await edit.WriteAsync(solution, asked, cancellationToken);

		var firstChanged = edit.Outcome.ChangedFiles.FirstOrDefault() ?? string.Empty;

		progress?.Report("Compiling what changed", 90);

		await edit.VerifyAsync(firstChanged, EditVerification.WithDependents(solution, [.. changedProjects]), cancellationToken);

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

		if (subsumed > 0)
		{
			notices.Add($"{subsumed} site(s) sat inside another site's replacement, outside anything it captured, and were written over by it.");
		}

		notices.AddRange(summary.Notices);

		var diff = edit.Outcome.Diff;

		if (diff.Length > DiffCeiling)
		{
			notices.Add($"The diff is {diff.Length:N0} characters and was left out. Narrow filePaths to one file or directory to read it.");
			diff = string.Empty;
		}

		notices.AddRange(edit.Report());

		var result = new PatternRewriteResult
		{
			Revision = snapshot.Revision,
			Applied = edit.Applied,
			SitesMatched = summary.Matched,
			SitesRewritten = summary.Rewritten,
			SitesSkipped = summary.SkippedCount,
			SitesUnmatched = summary.UnmatchedCount,
			Rules = summary.Rules,
			Skipped = summary.Skipped,
			Unmatched = summary.Unmatched,
			Files = summary.Files,
			FileCount = summary.FileCount,
			Diff = diff,
			ChangedFiles = edit.Outcome.ChangedFiles,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			TotalErrorCount = edit.Verification.TotalCount,
			ProjectsChecked = edit.Verification.Projects,
			Notices = notices,
		};

		return new MutationResult<PatternRewriteResult>(result, edit.Kept);
	}

	/// <summary>
	/// What adds a rule set's imports to a rewritten tree: each namespace not already in scope, placed
	/// where the file puts its imports, which is what every other write here does. One the rewrite then
	/// does not use is taken out again by the engine.
	/// </summary>
	private static Func<SyntaxTree, CompilationUnitSyntax, CompilationUnitSyntax>? Importer(
		Project project,
		CSharpCompilation compilation,
		IReadOnlyList<string> usings,
		CancellationToken cancellationToken)
	{
		if (usings.Count == 0) return null;

		return (tree, rewritten) =>
		{
			var text = tree.GetText(cancellationToken);
			var rules = Whitespace.RulesFor(project, tree, text);
			var style = UsingStyle.For(project, tree, (CompilationUnitSyntax)tree.GetRoot(cancellationToken), rules.LineEnding);

			return UsingDirectives.Ensure(rewritten, compilation.GetSemanticModel(tree), usings, style, cancellationToken).Root;
		};
	}

	/// <summary>
	/// A rewritten document formatted in both passes, and only where the rewrite wrote: Roslyn's
	/// formatter over each replacement, then the whitespace it leaves alone over the same spans, the
	/// same text written to every document linked to the file.
	/// </summary>
	private static async Task<Solution> FormattedAsync(Solution solution, DocumentId id, SyntaxNode rewritten, CancellationToken cancellationToken)
	{
		solution = solution.WithDocumentSyntaxRoot(id, rewritten);

		if (solution.GetDocument(id) is not { } document) return solution;

		// Each replacement's own span, not its full span: the indentation in front of it is the caller's,
		// and a formatter given the full span re-indents the line to its defaults where a repository has
		// no .editorconfig to say otherwise.
		var written = (await document.GetSyntaxRootAsync(cancellationToken))!.GetAnnotatedNodes(RewriteEngine.Replaced).Select(node => node.Span);
		var formatted = await Formatter.FormatAsync(document, written, cancellationToken: cancellationToken);
		var root = await formatted.GetSyntaxRootAsync(cancellationToken);
		var tree = await formatted.GetSyntaxTreeAsync(cancellationToken);
		var text = await formatted.GetTextAsync(cancellationToken);

		if (root is null || tree is null) return formatted.Project.Solution;

		var spans = root.GetAnnotatedNodes(RewriteEngine.Replaced).Select(node => node.FullSpan).ToArray();
		var final = Whitespace.Apply(root, text, Whitespace.RulesFor(formatted.Project, tree, text), spans);

		solution = formatted.Project.Solution;

		if (document.FilePath is not { Length: > 0 } path) return solution.WithDocumentText(id, final);

		foreach (var linked in solution.GetDocumentIdsWithFilePath(path))
		{
			solution = solution.WithDocumentText(linked, final);
		}

		return solution;
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

	/// <summary>Where a node starts, with its line of source and whether its project is a test project.</summary>
	private static SourceLocation Locate(SyntaxNode node, string path, SourceText text, bool isTest)
	{
		var position = text.Lines.GetLinePosition(node.SpanStart);

		return new SourceLocation
		{
			FilePath = path,
			Line = position.Line + 1,
			Column = position.Character + 1,
			Preview = text.Lines[position.Line].ToString().Trim(),
			IsTestProject = isTest,
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
