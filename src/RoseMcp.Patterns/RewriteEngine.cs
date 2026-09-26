using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>
/// Rewrites every site in a compilation's matched documents, and puts back each one whose replacement
/// does not compile there.
/// <para>
/// Every replacement is written in memory and the changed trees compiled; each error they did not have
/// before is charged to a site, those sites are put back, and the round repeats until a round adds no
/// errors. A site is charged by what the error can be traced to: the replacement that contains it;
/// else a replacement in the statement it is in, since <c>var x = Assert.Single(xs)</c> breaks on the
/// declaration as a whole rather than inside the call; else the replacement that declared a local the
/// erroring code uses; else every replacement in the member it is in. A tree still gaining errors when
/// the rounds run out is left as it was.
/// </para>
/// <para>
/// A site that is put back does not fall through to a later rule. That is how a string check refused
/// by the string rule would land on the collection rule with its arguments reversed.
/// </para>
/// </summary>
public static class RewriteEngine
{
	/// <summary>
	/// The annotation on every replacement, which is what a host formats: the replacement, and nothing
	/// around it the rewrite did not write.
	/// </summary>
	public static readonly SyntaxAnnotation Replaced = new("RoseMcp.Patterns.Replaced");

	/// <summary>Rewrites <paramref name="documents"/>, whose sites were scanned in <paramref name="compilation"/>.</summary>
	/// <param name="compilation">The compilation the sites were matched in.</param>
	/// <param name="documents">Each tree and the sites a scan found in it.</param>
	/// <param name="imports">
	/// Adds the imports a rewritten tree may need, given the original tree and the rewritten root. An
	/// added import the compiler then reports unnecessary is taken out again.
	/// </param>
	/// <param name="cancellationToken">Stops between trees and between rounds.</param>
	/// <param name="rounds">
	/// How many rounds of putting sites back before a tree still gaining errors is left as it was.
	/// </param>
	public static IReadOnlyList<DocumentRewrite> Run(
		CSharpCompilation compilation,
		IReadOnlyList<DocumentSites> documents,
		Func<SyntaxTree, CompilationUnitSyntax, CompilationUnitSyntax>? imports,
		CancellationToken cancellationToken,
		int rounds = 4)
	{
		var plans = documents.Select(document => new Plan(document)).ToList();

		foreach (var plan in plans)
		{
			plan.Baseline = Errors(compilation.GetSemanticModel(plan.Document.Tree).GetDiagnostics(cancellationToken: cancellationToken));
		}

		for (var round = 1; round <= rounds; round++)
		{
			var current = Rewrite(compilation, plans, imports, cancellationToken);
			var gained = false;

			foreach (var plan in plans.Where(plan => plan.Rewritten is not null))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var model = current.GetSemanticModel(plan.Rewritten!);
				var fresh = Fresh(plan.Baseline, model.GetDiagnostics(cancellationToken: cancellationToken));

				foreach (var error in fresh)
				{
					foreach (var index in Charged(error, plan.Rewritten!.GetRoot(cancellationToken), model, cancellationToken))
					{
						plan.Put(index, error.Id, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
					}
				}

				gained |= fresh.Count > 0;
				plan.Remaining = fresh.FirstOrDefault();

				if (fresh.Count == 0 && imports is not null) plan.Unnecessary = UnnecessaryImports(plan, model, cancellationToken);
			}

			if (!gained) return [.. plans.Select(plan => plan.Outcome())];
		}

		// Out of rounds: whatever still gains errors is left exactly as it was -- and only that. A file
		// that converged keeps its rewrite; one that did not is no reason to take anyone else's.
		foreach (var plan in plans.Where(plan => plan.Rewritten is not null && plan.Remaining is not null))
		{
			foreach (var index in plan.Active.ToList())
			{
				plan.Put(index, "RoseMcp", $"its file still did not compile after {rounds} rounds of putting sites back, so none of it was written; {Describe(plan.Remaining)}");
			}
		}

		Rewrite(compilation, plans, imports, cancellationToken);

		return [.. plans.Select(plan => plan.Outcome())];
	}

	/// <summary>Every plan's tree rewritten with its active sites, and the compilation holding them.</summary>
	private static CSharpCompilation Rewrite(
		CSharpCompilation compilation,
		IReadOnlyList<Plan> plans,
		Func<SyntaxTree, CompilationUnitSyntax, CompilationUnitSyntax>? imports,
		CancellationToken cancellationToken)
	{
		var current = compilation;

		foreach (var plan in plans)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var tree = plan.Document.Tree;
			var rewriter = new SiteRewriter(plan.Document.Sites, plan.Active);
			var root = (CompilationUnitSyntax)rewriter.Visit(tree.GetRoot(cancellationToken))!;

			foreach (var (index, reason) in rewriter.Refused) plan.Put(index, "RoseMcp", reason);

			plan.Written = rewriter.Written;

			if (rewriter.Written.Count == 0)
			{
				plan.Rewritten = null;

				continue;
			}

			if (imports is not null) root = imports(tree, root);

			plan.Rewritten = tree.WithRootAndOptions(root, tree.Options);
			current = current.ReplaceSyntaxTree(tree, plan.Rewritten);
		}

		return current;
	}

	/// <summary>An error as a reason names it: its id, its line in the rewritten file, and its message.</summary>
	private static string Describe(Diagnostic? error)
	{
		if (error is null) return "the last round's errors were not recorded";

		var line = error.Location.GetLineSpan().StartLinePosition.Line + 1;

		return $"the last error left was {error.Id} at line {line} of the rewritten file: {error.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
	}

	/// <summary>The sites an error is charged to, by the first of the traces that finds any.</summary>
	private static IEnumerable<int> Charged(Diagnostic error, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
	{
		var span = error.Location.SourceSpan;
		var sites = root.GetAnnotatedNodes(SiteRewriter.SiteKind).ToList();

		var containing = sites.Where(site => site.Span.Contains(span)).OrderBy(site => site.Span.Length).FirstOrDefault();

		if (containing is not null) return [Index(containing)];

		var node = root.FindNode(span, getInnermostNodeForTie: true);

		if (node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() is { } statement && Within(statement, sites) is { Count: > 0 } inStatement)
		{
			return inStatement;
		}

		foreach (var identifier in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
		{
			if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is not ILocalSymbol local) continue;

			foreach (var reference in local.DeclaringSyntaxReferences.Where(reference => reference.SyntaxTree == root.SyntaxTree))
			{
				var declaration = reference.GetSyntax(cancellationToken).AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();

				if (declaration is not null && Within(declaration, sites) is { Count: > 0 } declared) return declared;
			}
		}

		var member = node.AncestorsAndSelf().FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or LocalFunctionStatementSyntax);

		return member is not null && Within(member, sites) is { Count: > 0 } inMember ? inMember : [.. sites.Select(Index)];
	}

	/// <summary>The sites inside <paramref name="container"/>.</summary>
	private static List<int> Within(SyntaxNode container, IEnumerable<SyntaxNode> sites) =>
		[.. sites.Where(site => container.Span.Contains(site.Span)).Select(Index)];

	/// <summary>The site an annotated replacement stands for.</summary>
	private static int Index(SyntaxNode replacement) =>
		int.Parse(replacement.GetAnnotations(SiteRewriter.SiteKind).First().Data!, System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>The errors among <paramref name="diagnostics"/>, counted by id and message.</summary>
	private static Dictionary<(string, string), int> Errors(IEnumerable<Diagnostic> diagnostics)
	{
		var counts = new Dictionary<(string, string), int>();

		foreach (var error in diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			var key = (error.Id, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));

			counts[key] = counts.GetValueOrDefault(key) + 1;
		}

		return counts;
	}

	/// <summary>
	/// The errors in <paramref name="diagnostics"/> the original did not have, matched by id and message
	/// rather than position, since every position after the first replacement has moved.
	/// </summary>
	private static List<Diagnostic> Fresh(Dictionary<(string, string), int> baseline, IEnumerable<Diagnostic> diagnostics)
	{
		var remaining = new Dictionary<(string, string), int>(baseline);
		var fresh = new List<Diagnostic>();

		foreach (var error in diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			var key = (error.Id, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));

			if (remaining.GetValueOrDefault(key) > 0)
			{
				remaining[key]--;
			}
			else
			{
				fresh.Add(error);
			}
		}

		return fresh;
	}

	/// <summary>The imports this rewrite added that the compiler reports nothing uses.</summary>
	private static IReadOnlyList<string> UnnecessaryImports(Plan plan, SemanticModel model, CancellationToken cancellationToken)
	{
		var original = ((CompilationUnitSyntax)plan.Document.Tree.GetRoot(cancellationToken)).Usings.Select(directive => directive.ToString()).ToHashSet(StringComparer.Ordinal);
		var root = (CompilationUnitSyntax)plan.Rewritten!.GetRoot(cancellationToken);

		var unused = model.GetDiagnostics(cancellationToken: cancellationToken)
			.Where(diagnostic => diagnostic.Id == "CS8019")
			.Select(diagnostic => diagnostic.Location.SourceSpan)
			.ToList();

		return [.. root.Usings
			.Where(directive => !original.Contains(directive.ToString()) && unused.Any(span => directive.Span.Contains(span)))
			.Select(directive => directive.ToString())];
	}

	/// <summary>One tree's rewrite in progress: which of its sites are still written, and why the others are not.</summary>
	private sealed class Plan(DocumentSites document)
	{
		private readonly Dictionary<int, (string Id, string Reason)> _put = [];

		public DocumentSites Document { get; } = document;

		public HashSet<int> Active { get; } = [.. Enumerable.Range(0, document.Sites.Count).Where(index => !Subsumed(document.Sites, index))];

		public Dictionary<(string, string), int> Baseline { get; set; } = [];

		public SyntaxTree? Rewritten { get; set; }

		public IReadOnlyDictionary<int, string> Written { get; set; } = new Dictionary<int, string>();

		public IReadOnlyList<string> Unnecessary { get; set; } = [];

		/// <summary>The first error the last round left, for the reason given when the rounds run out.</summary>
		public Diagnostic? Remaining { get; set; }

		/// <summary>Puts a site back, keeping the first reason it was given.</summary>
		public void Put(int index, string id, string reason)
		{
			Active.Remove(index);
			_put.TryAdd(index, (id, reason));
		}

		/// <summary>What became of the tree and each of its sites.</summary>
		public DocumentRewrite Outcome()
		{
			var outcomes = Document.Sites.Select((site, index) =>
				_put.TryGetValue(index, out var put)
					? new SiteOutcome(site, SiteState.Skipped, null, put.Id, put.Reason)
					: Active.Contains(index) && Written.TryGetValue(index, out var written)
						? new SiteOutcome(site, SiteState.Rewritten, written, null, null)
						: new SiteOutcome(site, SiteState.Subsumed, null, null, null));

			var root = Rewritten?.GetRoot() as CompilationUnitSyntax;

			if (root is not null && Unnecessary.Count > 0)
			{
				root = root.WithUsings([.. root.Usings.Where(directive => !Unnecessary.Contains(directive.ToString()))]);
			}

			return new DocumentRewrite(Document.Tree, root, [.. outcomes]);
		}

		/// <summary>
		/// Whether a site sits inside another site but outside everything that site captured, so the outer
		/// replacement writes over it and it has nothing of its own to write.
		/// </summary>
		private static bool Subsumed(IReadOnlyList<PatternSite> sites, int index)
		{
			var site = sites[index];

			return sites.Any(outer => outer != site
				&& outer.Node.Span.Contains(site.Node.Span)
				&& !outer.Captures.Values.Any(capture => capture.Node is { } captured && captured.Span.Contains(site.Node.Span)));
		}
	}
}

/// <summary>One tree and the sites a scan found in it.</summary>
/// <param name="Tree">The tree, from the compilation the sites were matched in.</param>
/// <param name="Sites">The sites, as the scan returned them.</param>
public sealed record DocumentSites(SyntaxTree Tree, IReadOnlyList<PatternSite> Sites);

/// <summary>What became of one tree.</summary>
/// <param name="Original">The tree as it was.</param>
/// <param name="Root">
/// The rewritten root, with each replacement carrying <see cref="RewriteEngine.Replaced"/>; null when
/// nothing in the tree was written.
/// </param>
/// <param name="Sites">What became of each of its sites, in the order the scan found them.</param>
public sealed record DocumentRewrite(SyntaxTree Original, CompilationUnitSyntax? Root, IReadOnlyList<SiteOutcome> Sites);

/// <summary>What became of one site.</summary>
/// <param name="Site">The site.</param>
/// <param name="State">Whether it was written.</param>
/// <param name="After">Its replacement, as written before formatting, when it was written.</param>
/// <param name="DiagnosticId">The compiler's id for why it was put back, or RoseMcp for a reason of this assembly's own.</param>
/// <param name="Reason">Why it was put back.</param>
public sealed record SiteOutcome(PatternSite Site, SiteState State, string? After, string? DiagnosticId, string? Reason);

/// <summary>What became of a site.</summary>
public enum SiteState
{
	/// <summary>Its replacement was written.</summary>
	Rewritten,

	/// <summary>Its replacement would not compile there, or could not be built, and it was left as it was.</summary>
	Skipped,

	/// <summary>It sits inside another site's replacement, which writes over it.</summary>
	Subsumed,
}
