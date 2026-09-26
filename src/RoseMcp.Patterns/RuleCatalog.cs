using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.Patterns;

/// <summary>
/// An ordered list of rules, parsed and checked, independent of any compilation. At each site the
/// first rule that matches wins, so a specific rule goes before a general one.
/// </summary>
public sealed class RuleCatalog
{
	private RuleCatalog(IReadOnlyList<Rule> rules, IReadOnlyList<string> usings)
	{
		Rules = rules;
		Usings = usings;
	}

	/// <summary>The rules, in the order they are tried.</summary>
	public IReadOnlyList<Rule> Rules { get; }

	/// <summary>
	/// What the patterns are written against beyond what a project already imports: namespaces, static
	/// imports and aliases, each as a using directive would name it.
	/// </summary>
	public IReadOnlyList<string> Usings { get; }

	/// <summary>Parses every rule, or refuses the first one that cannot be used with a message that says what to change.</summary>
	/// <param name="rules">The rules in the order they are to be tried.</param>
	/// <param name="usings">
	/// Namespaces the patterns are written against, as a using directive names them:
	/// <c>Shouldly</c>, <c>static System.Math</c>, <c>Json = System.Text.Json</c>.
	/// </param>
	public static RuleCatalog Parse(IReadOnlyList<RuleText> rules, IReadOnlyList<string>? usings = null)
	{
		if (rules.Count == 0)
		{
			throw new PatternException("There are no rules. Each rule is a find and a replace, such as "
				+ "{\"find\": \"Assert.Equal($e$, $a$)\", \"replace\": \"$a$.ShouldBe($e$)\"}.");
		}

		var parsed = rules.Select((rule, index) => Rule.Parse(rule, index + 1)).ToList();
		var directives = (usings ?? []).Select(Directive).ToList();

		return new RuleCatalog(parsed, directives);
	}

	/// <summary>
	/// Every rule bound in <paramref name="compilation"/>. A rule that names something the compilation
	/// does not have is left unbound there, with the reason; a rule that an earlier rule shadows is
	/// refused outright, since it could never match anywhere.
	/// </summary>
	public BoundCatalog Bind(CSharpCompilation compilation, CancellationToken cancellationToken = default) =>
		PatternBinder.Bind(this, compilation, cancellationToken);

	/// <summary>One using, checked by parsing the directive it would be.</summary>
	private static string Directive(string written)
	{
		var trimmed = written.Trim().TrimEnd(';').Trim();
		var unit = SyntaxFactory.ParseCompilationUnit($"using {trimmed};");

		var isOneDirective = unit.Usings.Count == 1
			&& unit.Members.Count == 0
			&& !unit.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

		if (!isOneDirective)
		{
			throw new PatternException(
				$"'{written}' is not something a using directive can name. Write a namespace (Shouldly), a static "
				+ "import (static System.Math), or an alias (Json = System.Text.Json).");
		}

		return trimmed;
	}
}

/// <summary>
/// A rule set bound in one compilation: the rules that bind there, and why each of the others does not.
/// </summary>
public sealed class BoundCatalog
{
	internal BoundCatalog(
		RuleCatalog rules,
		CSharpCompilation compilation,
		IReadOnlyList<BoundRule> bound,
		IReadOnlyDictionary<int, string> unbound)
	{
		Rules = rules;
		Compilation = compilation;
		Bound = bound;
		Unbound = unbound;
		Usings = [.. rules.Usings.Where(Resolves)];
	}

	/// <summary>The rules this was bound from.</summary>
	public RuleCatalog Rules { get; }

	/// <summary>The compilation they were bound in, which is the one a scan's models have to come from.</summary>
	public CSharpCompilation Compilation { get; }

	/// <summary>The rules that bind here, in order.</summary>
	public IReadOnlyList<BoundRule> Bound { get; }

	/// <summary>Why each rule that does not bind here does not, by rule number.</summary>
	public IReadOnlyDictionary<int, string> Unbound { get; }

	/// <summary>
	/// The catalog's usings whose target this compilation has. A catalog is written against every project
	/// in scope, and a namespace one project cannot see is not one a rewrite there can use: importing it
	/// anyway is CS0234 on a line outside every site, which puts every site in the file back.
	/// </summary>
	public IReadOnlyList<string> Usings { get; }

	/// <summary>
	/// Whether the directive's namespace, type or alias target binds in <see cref="Compilation"/>, from the
	/// top of a file, where a using directive would go.
	/// </summary>
	private bool Resolves(string directive)
	{
		if (Compilation.SyntaxTrees.FirstOrDefault() is not { } tree) return false;

		var target = SyntaxFactory.ParseCompilationUnit($"using {directive};").Usings[0].NamespaceOrType;
		var symbol = Compilation.GetSemanticModel(tree)
			.GetSpeculativeSymbolInfo(0, target, SpeculativeBindingOption.BindAsTypeOrNamespace)
			.Symbol;

		return symbol is not null;
	}

	/// <summary>
	/// Every site in <paramref name="model"/>'s tree that a rule matches, and every call into the same
	/// types that none does.
	/// </summary>
	/// <param name="model">A model of one tree, from <see cref="Compilation"/>.</param>
	/// <param name="cancellationToken">Stops the scan between invocations.</param>
	public ScanResult Scan(SemanticModel model, CancellationToken cancellationToken = default) =>
		SiteScanner.Scan(this, model, cancellationToken);
}

/// <summary>One rule, bound in one compilation.</summary>
public sealed class BoundRule
{
	internal BoundRule(Rule rule, InvocationNode root)
	{
		Rule = rule;
		Root = root;
	}

	/// <summary>The rule.</summary>
	public Rule Rule { get; }

	/// <summary>
	/// The overloads the rule's find covers, as original definitions: what a caller reads to learn which
	/// of a method's overloads a rule reaches without having to guess from its text.
	/// </summary>
	public IReadOnlyList<IMethodSymbol> Methods => [.. Root.Candidates.Select(candidate => candidate.Method)];

	/// <summary>The find's call, bound.</summary>
	internal InvocationNode Root { get; }
}
