using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading what a caller asked to import, which happens before any file is opened. A static import
/// read as a name comes back as a tree with a parse error in it whose text still prints as valid C#,
/// so the file written from it is right and the compilation that verifies it is not -- which is why
/// every spelling here is read as the whole directive.
/// </summary>
public sealed class ImportDirectiveTests
{
	[Test]
	[Arguments("System.Text")]
	[Arguments("using System.Text")]
	[Arguments("using System.Text;")]
	[Arguments("  using System.Text ;  ")]
	public void Reads_every_spelling_of_a_namespace_as_the_same_import(string requested)
	{
		var import = ImportDirective.Parse(requested);

		Assert.Equal(ImportKind.Namespace, import.Kind);
		Assert.Equal("System.Text", import.Text);
	}

	[Test]
	[Arguments("static System.Math")]
	[Arguments("using static System.Math;")]
	[Arguments("using  static   System.Math")]
	public void Reads_a_static_import_as_one(string requested)
	{
		var import = ImportDirective.Parse(requested);

		Assert.Equal(ImportKind.Static, import.Kind);
		Assert.Equal("System.Math", import.Target);
		Assert.Equal("static System.Math", import.Text);
	}

	[Test]
	public void Reads_an_alias_as_one()
	{
		var import = ImportDirective.Parse("using Json = System.Text.Json;");

		Assert.Equal(ImportKind.Alias, import.Kind);
		Assert.Equal("Json", import.Alias);
		Assert.Equal("System.Text.Json", import.Target);
		Assert.Equal("Json = System.Text.Json", import.Text);
	}

	/// <summary>
	/// Anything that is not exactly one import is refused by name, rather than turned into text that
	/// happens to compile or into a tree that happens not to.
	/// </summary>
	[Test]
	[Arguments("static")]
	[Arguments("using ;")]
	[Arguments("System..Text")]
	[Arguments("System.Text; using System.IO")]
	[Arguments("class Imported { }")]
	public void Refuses_what_is_not_exactly_one_import(string requested)
	{
		var error = Assert.Throws<ArgumentException>(() => ImportDirective.Parse(requested));

		Assert.Contains($"'{requested}' is not an import", error.Message, StringComparison.Ordinal);
	}

	/// <summary>A global using belongs to the project, and writing one into an arbitrary file hides it there.</summary>
	[Test]
	public void Refuses_a_global_using()
	{
		var error = Assert.Throws<ArgumentException>(() => ImportDirective.Parse("global using System.Text;"));

		Assert.Contains("is a global using", error.Message, StringComparison.Ordinal);
	}

	[Test]
	[Arguments("System.Text", "using System.Text;\n")]
	[Arguments("static   System.Math", "using static System.Math;\n")]
	[Arguments("Json=System.Text.Json", "using Json = System.Text.Json;\n")]
	public void Writes_the_directive_it_read(string requested, string written)
	{
		Assert.Equal(written, ImportDirective.Parse(requested).ToSyntax("\n").ToFullString());
	}

	/// <summary>
	/// A directive already in the file reads as the same import a caller would name, which is what
	/// lets "already imported here" compare like with like.
	/// </summary>
	[Test]
	public void Reads_an_existing_directive_as_the_import_a_caller_would_name()
	{
		var unit = SyntaxFactory.ParseCompilationUnit("using static  System.Math;\nusing System;\nglobal using X = System;\n");

		Assert.Equal("static System.Math", ImportDirective.From(unit.Usings[0]).Text);
		Assert.Equal("System", ImportDirective.From(unit.Usings[1]).Text);
		Assert.Equal("X = System", ImportDirective.From(unit.Usings[2]).Text);
		Assert.True(ImportDirective.From(unit.Usings[2]).Global);
	}
}
