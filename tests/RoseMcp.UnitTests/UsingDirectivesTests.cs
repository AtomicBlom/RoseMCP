using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.UnitTests;

/// <summary>
/// Placing imports among a file's own, over a compilation built in memory so the scope questions are
/// the compiler's real answers and nothing else runs.
/// </summary>
public sealed class UsingDirectivesTests
{
	private static readonly UsingStyle Style = new() { SystemFirst = true, SeparateGroups = true, LineEnding = "\n" };

	/// <summary>
	/// Several imports into a file shorter than they are. Each one inserted grows the file the next is
	/// placed into, while the model answering what is in scope still holds the file as it was, so a
	/// position read off the grown file runs past the end of the model's and the compiler throws.
	/// </summary>
	[Test]
	public void Ensures_several_imports_into_a_file_shorter_than_they_are()
	{
		var (root, model) = Compile("public sealed record Point(int X, int Y);\n");

		var insertion = UsingDirectives.Ensure(
			root,
			model,
			["System.Text.Json", "System.Text.Json.Serialization", "System.Collections.Immutable"],
			Style,
			TestContext.Current!.Execution.CancellationToken);

		insertion.Added.Count.ShouldBe(3);

		insertion.Root.ToFullString().ShouldBe(
			"using System.Collections.Immutable;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;\n\n"
				+ "public sealed record Point(int X, int Y);\n");
	}

	/// <summary>
	/// A file keeping its imports inside a namespace block has them placed there, at the block's depth
	/// and in its order and grouping. Asked at file level instead, an import the block already has is
	/// written a second time above it, which is IDE0005, and any other lands apart from the rest.
	/// </summary>
	[Test]
	public void Places_imports_among_those_a_namespace_block_keeps()
	{
		var (root, model) = Compile(
			"namespace Probe\n{\n\tusing System.Text;\n\n\tpublic static class Coded\n\t{\n"
				+ "\t\tpublic static Encoding Utf8 => Encoding.UTF8;\n\t}\n}\n");

		var insertion = UsingDirectives.Ensure(
			root,
			model,
			["System.Text", "Probe.Extras", "System.Globalization"],
			Style,
			TestContext.Current!.Execution.CancellationToken);

		insertion.Added.ShouldBe(["Probe.Extras", "System.Globalization"]);
		insertion.AlreadyInScope.ShouldBe(["System.Text: already imported here"]);

		insertion.Root.ToFullString().ShouldBe(
			"namespace Probe\n{\n\tusing System.Globalization;\n\tusing System.Text;\n\n\tusing Probe.Extras;\n\n"
				+ "\tpublic static class Coded\n\t{\n\t\tpublic static Encoding Utf8 => Encoding.UTF8;\n\t}\n}\n");
	}

	/// <summary>An import a namespace block brings in is in scope, whichever namespace it is.</summary>
	[Test]
	public void Counts_an_import_a_namespace_block_carries_as_in_scope()
	{
		var (root, model) = Compile("namespace Probe\n{\n\tusing System.Text;\n\n\tpublic static class Coded\n\t{\n\t}\n}\n");

		UsingDirectives.AlreadyInScope(
				root, model, ImportDirective.Parse("System.Text"), TestContext.Current!.Execution.CancellationToken)
			.ShouldBe("already imported here");
	}

	/// <summary>
	/// With two sibling namespace blocks, the first one's imports are not in scope in the second, so
	/// an import goes to the top of the file where both see it, and one only the first block carries is
	/// not reported as already there.
	/// </summary>
	[Test]
	public void Imports_at_file_level_where_the_file_holds_sibling_namespace_blocks()
	{
		var (root, model) = Compile(
			"namespace First\n{\n\tusing System.Text;\n\n\tpublic static class A\n\t{\n\t}\n}\n\n"
				+ "namespace Second\n{\n\tpublic static class B\n\t{\n\t}\n}\n");

		var insertion = UsingDirectives.Ensure(
			root, model, ["System.Text"], Style, TestContext.Current!.Execution.CancellationToken);

		insertion.Added.ShouldBe(["System.Text"]);
		insertion.AlreadyInScope.ShouldBeEmpty();

		insertion.Root.ToFullString().ShouldStartWith(
			"using System.Text;\n\nnamespace First\n{\n\tusing System.Text;\n", Case.Sensitive, insertion.Root.ToFullString());
	}

	/// <summary>Compiled against everything this test host runs on, which is every namespace these name.</summary>
	private static (CompilationUnitSyntax Root, SemanticModel Model) Compile(string source)
	{
		var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

		var tree = CSharpSyntaxTree.ParseText(source);

		var compilation = CSharpCompilation.Create(
			"Probe",
			[tree],
			platform,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		return ((CompilationUnitSyntax)tree.GetRoot(), compilation.GetSemanticModel(tree));
	}
}
