using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Patterns;

/// <summary>
/// What one placeholder captured at one site: the caller's own syntax, never regenerated, so a
/// replacement carries its text, trivia and comments exactly as written.
/// </summary>
/// <param name="Name">The placeholder's name.</param>
/// <param name="Kind">What it stands for.</param>
/// <param name="Node">The captured expression, type or lambda body; null for an identifier.</param>
/// <param name="Token">The captured identifier; default for anything else.</param>
public sealed record Capture(string Name, PlaceholderKind Kind, SyntaxNode? Node, SyntaxToken Token)
{
	/// <summary>The captured code as it is written.</summary>
	public string Text => Node?.ToString() ?? Token.Text;
}

/// <summary>
/// One place a rule matches: the call, or the statement for a statement rule, with what each
/// placeholder captured there.
/// </summary>
/// <param name="Node">The invocation, or its expression statement for a statement rule.</param>
/// <param name="Rule">The first rule that matched, which is the one that wins the site.</param>
/// <param name="Captures">What each of its placeholders captured, by name.</param>
/// <param name="AlsoMatched">The numbers of later rules that matched the same site and lost to it.</param>
public sealed record PatternSite(
	SyntaxNode Node,
	Rule Rule,
	IReadOnlyDictionary<string, Capture> Captures,
	IReadOnlyList<int> AlsoMatched);

/// <summary>
/// A call into one of the types the rules are about that no rule matched: the list that says whether a
/// rule set is complete, since matched and unmatched calls together are every call there is.
/// </summary>
/// <param name="Node">The call.</param>
/// <param name="Method">The method it calls, as its original definition; the best candidate when it does not bind.</param>
/// <param name="Binds">Whether the call compiles at all, since one that does not can never be matched.</param>
public sealed record UnmatchedCall(InvocationExpressionSyntax Node, IMethodSymbol Method, bool Binds);

/// <summary>What a scan of one tree found.</summary>
/// <param name="Sites">Every site a rule matched, in document order.</param>
/// <param name="Unmatched">Every call into the rules' types that no rule matched, in document order.</param>
public sealed record ScanResult(IReadOnlyList<PatternSite> Sites, IReadOnlyList<UnmatchedCall> Unmatched);
