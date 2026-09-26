using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.Patterns;

namespace RoseMcp.UnitTests;

/// <summary>
/// A rule set bound and scanned over a few lines of source, so a test can say what a pattern matches
/// in a line.
/// <para>
/// The assertion library a pattern is written against is declared here in source, as the shapes of
/// its overloads, rather than referenced. What is under test is how a pattern meets a method group --
/// overloads that share a shape, optional parameters, a generic beside a non-generic -- and the shapes
/// say all of that; a reference to the real package would keep it in this project's graph for as long
/// as the test exists.
/// </para>
/// </summary>
internal static class PatternHarness
{
	/// <summary>
	/// The overload shapes the matcher meets in practice: xunit's, with the names and defaults its
	/// parameters really have, and an extension method for the two ways of calling one.
	/// </summary>
	private const string Stubs = """
		namespace Xunit
		{
			using System;
			using System.Collections;
			using System.Collections.Generic;

			public static class Assert
			{
				public static void Equal<T>(T expected, T actual) { }
				public static void Equal(string? expected, string? actual, bool ignoreCase = false, bool ignoreLineEndingDifferences = false, bool ignoreWhiteSpaceDifferences = false, bool ignoreAllWhiteSpace = false) { }
				public static void Equal(double expected, double actual, int precision) { }
				public static void True(bool condition) { }
				public static void True(bool? condition) { }
				public static void True(bool condition, string? userMessage) { }
				public static void Contains(string expectedSubstring, string? actualString) { }
				public static void Contains(string expectedSubstring, string? actualString, StringComparison comparisonType) { }
				public static void Contains<T>(T expected, IEnumerable<T> collection) { }
				public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter) { }
				public static TValue Contains<TKey, TValue>(TKey expected, IDictionary<TKey, TValue> collection) where TKey : notnull => default!;
				public static T Single<T>(IEnumerable<T> collection) => default!;
				public static object? Single(IEnumerable collection) => null;
				public static T Single<T>(IEnumerable<T> collection, Predicate<T> predicate) => default!;
				public static T Throws<T>(Action testCode) where T : Exception => default!;
				public static T Throws<T>(string? paramName, Action testCode) where T : ArgumentException => default!;
				public static void All<T>(IEnumerable<T> collection, Action<T> action) { }
				public static T NotNull<T>(T? @object) where T : class => @object!;
				public static void Fail(string message) { }
			}
		}

		namespace Shouldly
		{
			using System.Collections.Generic;

			public enum Case { Sensitive, Insensitive }

			public static class ShouldStubs
			{
				public static void ShouldBe<T>(this T actual, T expected) { }
				public static void ShouldBeTrue(this bool actual) { }
				public static void ShouldBeFalse(this bool actual) { }
				public static void ShouldContain(this string actual, string expected, Case caseSensitivity = Case.Insensitive) { }
				public static void ShouldContain<T>(this IEnumerable<T> actual, T expected) { }
				public static T ShouldHaveSingleItem<T>(this IEnumerable<T> actual) => default!;
			}
		}

		namespace Checks
		{
			public static class Extensions
			{
				public static void ShouldBe<T>(this T actual, T expected) { }
			}
		}
		""";

	/// <summary>What every test source starts with: the alias the repository's own test projects declare.</summary>
	private const string Prelude = """
		global using System;
		global using System.Collections.Generic;
		global using Assert = Xunit.Assert;

		""";

	/// <summary>
	/// The runtime's own assemblies and nothing else from beside this test, which references a real
	/// assertion library -- one that would bind wherever a stub was meant to, or where nothing was.
	/// </summary>
	private static readonly MetadataReference[] Platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
		.Split(Path.PathSeparator)
		.Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
		.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
		.ToArray();

	/// <summary>
	/// A rule written as its find and replace. A test about what a find matches leaves the replace out,
	/// and gets one that parses in the find's own shape.
	/// </summary>
	internal static RuleText Rule(string find, string? replace = null) =>
		new(find, replace ?? (find.TrimEnd().EndsWith(';') ? ";" : "null"));

	/// <summary>Scans <paramref name="source"/> with <paramref name="rules"/>, bound against the stubs.</summary>
	internal static ScanResult Scan(string source, params RuleText[] rules) => Scan(source, rules, []);

	/// <summary>The same, with namespaces the patterns are written against.</summary>
	internal static ScanResult Scan(string source, IReadOnlyList<RuleText> rules, IReadOnlyList<string> usings)
	{
		var (compilation, tree) = Compile(source, withStubs: true);

		return RuleCatalog.Parse(rules, usings).Bind(compilation).Scan(compilation.GetSemanticModel(tree));
	}

	/// <summary>The rule set bound over <paramref name="source"/>, with or without the stubs to bind to.</summary>
	internal static BoundCatalog Bind(string source, bool withStubs, params RuleText[] rules)
	{
		var (compilation, _) = Compile(source, withStubs);

		return RuleCatalog.Parse(rules).Bind(compilation);
	}

	/// <summary>
	/// <paramref name="source"/> rewritten by <paramref name="rules"/>, as text without the prelude, with
	/// what became of each site.
	/// </summary>
	internal static (string Text, IReadOnlyList<SiteOutcome> Sites) Rewrite(string source, params RuleText[] rules)
	{
		var (compilation, tree) = Compile(source, withStubs: true);
		var scan = RuleCatalog.Parse(rules).Bind(compilation).Scan(compilation.GetSemanticModel(tree));
		var rewrite = RewriteEngine.Run(compilation, [new DocumentSites(tree, scan.Sites)], imports: null, CancellationToken.None)[0];
		var text = (rewrite.Root ?? tree.GetRoot()).ToFullString();

		return (text[Prelude.Length..], rewrite.Sites);
	}

	/// <summary>
	/// Several sources rewritten together in one compilation, as a project's files are, with the engine
	/// given <paramref name="rounds"/> rounds.
	/// </summary>
	internal static IReadOnlyList<DocumentRewrite> RewriteEach(int rounds, IReadOnlyList<string> sources, params RuleText[] rules)
	{
		var options = new CSharpParseOptions(LanguageVersion.Latest);
		var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText((index == 0 ? Prelude : string.Empty) + source, options)).ToList();

		var compilation = CSharpCompilation.Create(
			"Patterns",
			[CSharpSyntaxTree.ParseText(Stubs, options), .. trees],
			Platform,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		var bound = RuleCatalog.Parse(rules).Bind(compilation);
		var documents = trees.Select(tree => new DocumentSites(tree, bound.Scan(compilation.GetSemanticModel(tree)).Sites)).ToList();

		return RewriteEngine.Run(compilation, documents, imports: null, CancellationToken.None, rounds);
	}

	/// <summary>
	/// One site as a line a test can compare: the rule that won, the code it matched, and each capture.
	/// </summary>
	internal static string Describe(PatternSite site)
	{
		var captures = site.Captures.Values
			.OrderBy(capture => capture.Name, StringComparer.Ordinal)
			.Select(capture => $"{capture.Name}={capture.Text}");

		return $"{site.Rule.Number}: {site.Node} [{string.Join(", ", captures)}]";
	}

	/// <summary>Every site of a scan, described.</summary>
	internal static IReadOnlyList<string> Sites(ScanResult result) => [.. result.Sites.Select(Describe)];

	/// <summary>
	/// A compilation of <paramref name="source"/> that compiles clean, so a fixture that does not is
	/// reported as a broken fixture rather than as a pattern that failed to match.
	/// </summary>
	private static (CSharpCompilation Compilation, SyntaxTree Tree) Compile(string source, bool withStubs)
	{
		var options = new CSharpParseOptions(LanguageVersion.Latest);
		var tree = CSharpSyntaxTree.ParseText(Prelude + source, options);

		IEnumerable<SyntaxTree> trees = withStubs ? [CSharpSyntaxTree.ParseText(Stubs, options), tree] : [tree];

		var compilation = CSharpCompilation.Create(
			"Patterns",
			trees,
			Platform,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		if (withStubs)
		{
			var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();

			if (errors.Count > 0) throw new InvalidOperationException("The fixture does not compile: " + string.Join("; ", errors));
		}

		return (compilation, tree);
	}
}
