using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoseMcp.UnitTests;

/// <summary>
/// How the code uses a name that did not bind, read off the syntax around it. It decides what kind
/// of symbol an import could bring in, so a call is never answered with a type.
/// </summary>
public sealed class NameUseTests
{
	[Test]
	[Arguments("var group = Group(call, name);", NameUse.Invoked)]
	[Arguments("var group = Group<int>(call);", NameUse.Invoked)]
	[Arguments("var loud = text.Group();", NameUse.Member)]
	[Arguments("var loud = text?.Group();", NameUse.Member)]
	[Arguments("var loud = text.Group;", NameUse.Member)]
	[Arguments("Group group = null;", NameUse.Type)]
	[Arguments("var group = new Group();", NameUse.Type)]
	[Arguments("var kind = typeof(Group);", NameUse.Type)]
	[Arguments("var name = Group.Name;", NameUse.Any)]
	[Arguments("var group = Group;", NameUse.Any)]
	public void Reads_how_the_name_is_used(string statement, NameUse expected)
	{
		var tree = CSharpSyntaxTree.ParseText($"class C {{ void M(string text, object call, object name) {{ {statement} }} }}");

		var token = tree.GetRoot().DescendantTokens()
			.First(token => token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "Group");

		NameUses.Of(token).ShouldBe(expected);
	}
}
