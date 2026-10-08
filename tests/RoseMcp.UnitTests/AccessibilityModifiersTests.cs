using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// Changing who may see a declaration, which is one keyword in a modifier list and nothing else. The
/// other modifiers are the member's own, and the documentation comment and indentation in front of the
/// first token belong to whichever token ends up first -- so a change that moved either would be a
/// one-word request that rewrote a line and a half.
/// </summary>
public sealed class AccessibilityModifiersTests
{
	/// <summary>
	/// The keyword is replaced where it stands, the modifiers around it stay in the file's own order,
	/// and where there was none the new one goes first and takes over the leading trivia: a
	/// documentation comment, an attribute's line, or the indentation alone.
	/// </summary>
	[Test]
	[Arguments("\t/// <summary>Doc.</summary>\n\tprivate static int Count() => 1;", Accessibility.Internal,
		"\t/// <summary>Doc.</summary>\n\tinternal static int Count() => 1;")]
	[Arguments("\tstatic private int Count() => 1;", Accessibility.Public, "\tstatic public int Count() => 1;")]
	[Arguments("\tprotected internal virtual void Run() { }", Accessibility.ProtectedAndInternal,
		"\tprivate protected virtual void Run() { }")]
	[Arguments("\tprivate void Run() { }", Accessibility.ProtectedOrInternal, "\tprotected internal void Run() { }")]
	[Arguments("\t/// <summary>Doc.</summary>\n\tvoid Run() { }", Accessibility.Public,
		"\t/// <summary>Doc.</summary>\n\tpublic void Run() { }")]
	[Arguments("\t[Obsolete]\n\tvoid Run() { }", Accessibility.Public, "\t[Obsolete]\n\tpublic void Run() { }")]
	[Arguments("\tstatic void Run() { }", Accessibility.Internal, "\tinternal static void Run() { }")]
	[Arguments("\tprivate readonly int _count;", Accessibility.Internal, "\tinternal readonly int _count;")]
	[Arguments("\tclass Nested { }", Accessibility.Private, "\tprivate class Nested { }")]
	public void Changes_only_the_accessibility_keywords(string member, Accessibility accessibility, string expected)
	{
		var root = SyntaxFactory.ParseCompilationUnit($"class C\n{{\n{member}\n}}\n");
		var declaration = root.DescendantNodes().OfType<MemberDeclarationSyntax>().Skip(1).First();

		var written = AccessibilityModifiers.With(
			declaration, AccessibilityModifiers.KeywordsFor(accessibility), new SyntaxAnnotation());

		written.ToFullString().ShouldBe(expected + "\n");
	}

	/// <summary>
	/// The two-word forms compile either way round, so either is accepted, and anything else is
	/// refused with the six that are.
	/// </summary>
	[Test]
	[Arguments("internal", Accessibility.Internal)]
	[Arguments(" Public ", Accessibility.Public)]
	[Arguments("protected internal", Accessibility.ProtectedOrInternal)]
	[Arguments("internal protected", Accessibility.ProtectedOrInternal)]
	[Arguments("private protected", Accessibility.ProtectedAndInternal)]
	[Arguments("protected  private", Accessibility.ProtectedAndInternal)]
	public void Reads_an_accessibility_however_it_is_written(string written, Accessibility expected) =>
		AccessibilityModifiers.Parse(written).ShouldBe(expected);

	[Test]
	public void Refuses_what_is_not_an_accessibility()
	{
		var error = Should.Throw<ArgumentException>(() => AccessibilityModifiers.Parse("friend"));

		error.Message.ShouldContain("'friend' is not an accessibility", Case.Sensitive);
		error.Message.ShouldContain("private protected", Case.Sensitive);
	}
}
