using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// A replacement documented with only a <c>&lt;remarks&gt;</c> or a <c>&lt;param&gt;</c> is not a request
/// to delete the summary, and losing it compiles clean.
/// </summary>
public sealed class DocumentationSummaryTests
{
	private const string Existing = """
		class Holder
		{
			/// <summary>
			/// What the widget is for.
			/// </summary>
			/// <param name="size">Old.</param>
			public void Widget(int size) { }
		}
		""";

	[Test]
	public void Keeps_the_summary_when_the_replacement_documents_only_its_parameters()
	{
		var result = Kept("""
			/// <param name="count">How many.</param>
			public void Widget(int count) { }
			""", out var kept);

		Assert.True(kept);
		Assert.Contains("What the widget is for.", result, StringComparison.Ordinal);
		Assert.Contains("<param name=\"count\">How many.</param>", result, StringComparison.Ordinal);

		// The old parameter documentation goes, because it names a parameter the member no longer has.
		Assert.DoesNotContain("Old.", result, StringComparison.Ordinal);
		Assert.True(
			result.IndexOf("<summary>", StringComparison.Ordinal) < result.IndexOf("<param", StringComparison.Ordinal),
			"the summary comes first, where every documentation comment puts it");
	}

	[Test]
	public void Leaves_a_replacement_that_has_its_own_summary_alone()
	{
		var result = Kept("""
			/// <summary>Something else.</summary>
			public void Widget(int size) { }
			""", out var kept);

		Assert.False(kept);
		Assert.DoesNotContain("What the widget is for.", result, StringComparison.Ordinal);
	}

	/// <summary><c>&lt;inheritdoc/&gt;</c> is the deliberate way to have no summary.</summary>
	[Test]
	public void Leaves_a_replacement_that_inherits_its_documentation_alone()
	{
		var result = Kept("""
			/// <inheritdoc/>
			public void Widget(int size) { }
			""", out var kept);

		Assert.False(kept);
		Assert.DoesNotContain("What the widget is for.", result, StringComparison.Ordinal);
	}

	/// <summary>The result has to parse back as one documentation comment, not as stray text.</summary>
	[Test]
	public void Produces_a_single_documentation_comment()
	{
		var result = Kept("""
			/// <remarks>More.</remarks>
			public void Widget(int size) { }
			""", out _);

		var parsed = SyntaxFactory.ParseLeadingTrivia(result)
			.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
			.ToArray();

		var comment = (DocumentationCommentTriviaSyntax)Assert.Single(parsed).GetStructure()!;
		Assert.Equal(
			["summary", "remarks"],
			comment.Content.OfType<XmlElementSyntax>().Select(element => element.StartTag.Name.LocalName.Text));
	}

	private static string Kept(string replacement, out bool kept)
	{
		var existing = SyntaxFactory.ParseCompilationUnit(Existing)
			.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.Single();

		var supplied = SyntaxFactory.ParseMemberDeclaration(replacement)!;

		var trivia = DocumentationSummary.KeptInto(
			MemberSyntax.WithoutLeadingBlanks(supplied.GetLeadingTrivia()), existing.GetLeadingTrivia(), out kept);

		return string.Concat(trivia.Select(item => item.ToFullString()));
	}
}
