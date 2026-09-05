using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// Whether a project is a test project, read from what it references.
/// <para>
/// Asked of the compilation rather than of the project file, because <c>IsTestProject</c> is a
/// property MSBuild may set from an import and a parse would not see it -- and because the thing
/// that actually makes a project a test project is that a test framework is on its reference list.
/// </para>
/// <para>
/// It answers a question a caller has about every reference: a use from a test is a different fact
/// from a use in the product. It usually means the symbol can change and the test follows, which is
/// the difference between a list of forty call sites that all have to be thought about and one
/// where six do.
/// </para>
/// </summary>
public static class TestProjects
{
	/// <summary>
	/// Assembly names that mean tests. Matched on the reference rather than on a package id, since a
	/// project reaches a framework through whatever chain of packages it likes and the assembly is
	/// what ends up on the list either way.
	/// </summary>
	private static readonly string[] Frameworks =
	[
		"xunit.core",
		"xunit.v3.core",
		"xunit.assert",
		"nunit.framework",
		"Microsoft.VisualStudio.TestPlatform.TestFramework",
		"Microsoft.TestPlatform.TestFramework",
	];

	/// <summary>True where the project references a test framework.</summary>
	public static bool IsTest(Project project) =>
		project.MetadataReferences.Any(reference =>
			reference.Display is { Length: > 0 } display && Matches(Path.GetFileNameWithoutExtension(display)));

	private static bool Matches(string assembly) =>
		Frameworks.Contains(assembly, StringComparer.OrdinalIgnoreCase);
}
