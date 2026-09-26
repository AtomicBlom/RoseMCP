using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.Patterns;

/// <summary>
/// Rewrites one tree's sites, children first, so a site inside another's capture is rewritten before
/// the outer replacement takes it.
/// <para>
/// A pure function of the original tree and the set of sites to write, so a round that puts sites back
/// starts again from the original rather than patching a tree already rewritten: an outer loop whose
/// inner assertion was put back is simply rebuilt around the original inner call.
/// </para>
/// </summary>
internal sealed class SiteRewriter : CSharpSyntaxRewriter
{
	/// <summary>The kind of the annotation that names a replacement's site, by its index in the tree's site list.</summary>
	public const string SiteKind = "RoseMcp.Patterns.Site";

	private readonly IReadOnlyList<PatternSite> _sites;
	private readonly ISet<int> _active;
	private readonly Dictionary<SyntaxNode, int> _siteOf = [];
	private readonly HashSet<SyntaxNode> _captured = [];
	private readonly Dictionary<SyntaxNode, SyntaxNode> _rewritten = [];

	/// <summary>A rewriter for <paramref name="sites"/>, writing only those in <paramref name="active"/>.</summary>
	public SiteRewriter(IReadOnlyList<PatternSite> sites, ISet<int> active)
	{
		_sites = sites;
		_active = active;

		for (var index = 0; index < sites.Count; index++)
		{
			if (!active.Contains(index)) continue;

			_siteOf[sites[index].Node] = index;

			foreach (var capture in sites[index].Captures.Values)
			{
				if (capture.Node is { } node) _captured.Add(node);
			}
		}
	}

	/// <summary>Why each site whose replacement could not be written there could not, by index.</summary>
	public Dictionary<int, string> Refused { get; } = [];

	/// <summary>Each written site's replacement, as text, by index.</summary>
	public Dictionary<int, string> Written { get; } = [];

	public override SyntaxNode? Visit(SyntaxNode? node)
	{
		if (node is null) return null;

		var visited = base.Visit(node);

		if (visited is null) return null;

		var result = _siteOf.TryGetValue(node, out var index) ? Replace(node, visited, index) : visited;

		// Recorded after the node's own replacement, so a capture that is itself a site hands the outer
		// replacement what it became rather than what it was.
		if (_captured.Contains(node)) _rewritten[node] = result;

		return result;
	}

	/// <summary>The site at <paramref name="node"/> replaced, or <paramref name="visited"/> when its replacement cannot be built.</summary>
	private SyntaxNode Replace(SyntaxNode node, SyntaxNode visited, int index)
	{
		var site = _sites[index];

		var captures = site.Captures.ToDictionary(
			entry => entry.Key,
			entry => entry.Value.Node is { } captured
				? new CapturedSyntax(Carried(site.Node, captured), entry.Value.Token, LineBreak(site.Node, captured))
				: new CapturedSyntax(null, entry.Value.Token),
			StringComparer.Ordinal);

		var replacement = Template.Instantiate(site.Rule, captures, node, out var refusal);

		if (replacement is null)
		{
			Refused[index] = refusal ?? "its replacement could not be built";

			return visited;
		}

		Written[index] = replacement.ToString();

		return replacement
			.WithLeadingTrivia(node.GetLeadingTrivia())
			.WithTrailingTrivia(node.GetTrailingTrivia())
			.WithAdditionalAnnotations(RewriteEngine.Replaced, new SyntaxAnnotation(SiteKind, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
	}

	/// <summary>
	/// A capture as the replacement takes it: rewritten, with the comments written beside it inside the
	/// site. A comment before an argument is trailing trivia of the token before it, and one after is
	/// trailing trivia of the comma after it, so neither is part of the captured node -- and both tokens
	/// go when the site is replaced, taking the comment with them unless it moves with the capture.
	/// </summary>
	private SyntaxNode Carried(SyntaxNode site, SyntaxNode captured)
	{
		var rewritten = _rewritten.GetValueOrDefault(captured, captured);
		var before = captured.GetFirstToken().GetPreviousToken();
		var after = captured.GetLastToken().GetNextToken();

		if (site.Span.Contains(before.Span) && before.TrailingTrivia.Any(IsComment))
		{
			rewritten = rewritten.WithLeadingTrivia(before.TrailingTrivia.AddRange(rewritten.GetLeadingTrivia()));
		}

		if (site.Span.Contains(after.Span) && after.TrailingTrivia.Any(IsComment))
		{
			rewritten = rewritten.WithTrailingTrivia(rewritten.GetTrailingTrivia().AddRange(after.TrailingTrivia));
		}

		return rewritten;
	}

	/// <summary>
	/// The line break and indentation a capture began its line with, when it began one inside the site. The
	/// break is trailing trivia of the token before the capture, which goes with the site, so an argument
	/// the caller put on a line of its own would otherwise be pulled up onto a call that then runs off the
	/// screen. Empty when the capture shared its line, or when a comment before it already moves with it.
	/// </summary>
	private static SyntaxTriviaList LineBreak(SyntaxNode site, SyntaxNode captured)
	{
		var first = captured.GetFirstToken();
		var before = first.GetPreviousToken();

		var beginsLine = site.Span.Contains(before.Span)
			&& before.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia)
			&& !before.TrailingTrivia.Any(IsComment);

		if (!beginsLine) return default;

		var lineEnd = before.TrailingTrivia.Last(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
		var indentation = first.LeadingTrivia.Reverse().TakeWhile(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia)).Reverse();

		return SyntaxFactory.TriviaList([lineEnd, .. indentation]);
	}

	/// <summary>Whether a piece of trivia is a comment.</summary>
	private static bool IsComment(SyntaxTrivia trivia) =>
		trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);
}
