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

		import.Kind.ShouldBe(ImportKind.Namespace);
		import.Text.ShouldBe("System.Text");
	}

	[Test]
	[Arguments("static System.Math")]
	[Arguments("using static System.Math;")]
	[Arguments("using  static   System.Math")]
	public void Reads_a_static_import_as_one(string requested)
	{
		var import = ImportDirective.Parse(requested);

		import.Kind.ShouldBe(ImportKind.Static);
		import.Target.ShouldBe("System.Math");
		import.Text.ShouldBe("static System.Math");
	}

	[Test]
	public void Reads_an_alias_as_one()
	{
		var import = ImportDirective.Parse("using Json = System.Text.Json;");

		import.Kind.ShouldBe(ImportKind.Alias);
		import.Alias.ShouldBe("Json");
		import.Target.ShouldBe("System.Text.Json");
		import.Text.ShouldBe("Json = System.Text.Json");
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
		var error = Should.Throw<ArgumentException>(() => ImportDirective.Parse(requested)).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain($"'{requested}' is not an import", Case.Sensitive);
	}

	/// <summary>A global using belongs to the project, and writing one into an arbitrary file hides it there.</summary>
	[Test]
	public void Refuses_a_global_using()
	{
		var error = Should.Throw<ArgumentException>(() => ImportDirective.Parse("global using System.Text;")).ShouldBeOfType<ArgumentException>();

		error.Message.ShouldContain("is a global using", Case.Sensitive);
	}

	[Test]
	[Arguments("System.Text", "using System.Text;\n")]
	[Arguments("static   System.Math", "using static System.Math;\n")]
	[Arguments("Json=System.Text.Json", "using Json = System.Text.Json;\n")]
	public void Writes_the_directive_it_read(string requested, string written)
	{
		ImportDirective.Parse(requested).ToSyntax("\n").ToFullString().ShouldBe(written);
	}

	/// <summary>
	/// A directive already in the file reads as the same import a caller would name, which is what
	/// lets "already imported here" compare like with like.
	/// </summary>
	[Test]
	public void Reads_an_existing_directive_as_the_import_a_caller_would_name()
	{
		var unit = SyntaxFactory.ParseCompilationUnit("using static  System.Math;\nusing System;\nglobal using X = System;\n");

		ImportDirective.From(unit.Usings[0]).Text.ShouldBe("static System.Math");
		ImportDirective.From(unit.Usings[1]).Text.ShouldBe("System");
		ImportDirective.From(unit.Usings[2]).Text.ShouldBe("X = System");
		ImportDirective.From(unit.Usings[2]).Global.ShouldBeTrue();
	}
}
