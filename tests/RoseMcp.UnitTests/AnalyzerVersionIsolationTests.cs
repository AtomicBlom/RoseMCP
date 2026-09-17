using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging.Abstractions;


namespace RoseMcp.UnitTests;

/// <summary>
/// Two versions of one analyzer dependency must resolve to the version the analyzer asking for it
/// was built against.
/// <para>
/// A solution spanning several target frameworks carries a generator per framework, and the
/// versions differ -- one 96-project solution holds four versions of
/// <c>Microsoft.Extensions.Logging.Generators</c>. Resolved by simple name, whichever arrived last
/// answers for all of them, and the runtime rejects the mismatch with
/// <c>FUSION_E_REF_DEF_MISMATCH</c>. That surfaces as an analyzer that loads and produces nothing,
/// because a generator that throws on load is reported through an event and then quietly returns
/// no generators, while MSBuild goes on passing it to the compiler.
/// </para>
/// <para>
/// The assemblies are emitted here rather than staged as a fixture: what the failure turns on is
/// two assembly identities sharing a simple name, which is a property of the metadata and nothing
/// to do with MSBuild, and building a solution to obtain them would move a test that takes a
/// second into the suite that takes minutes.
/// </para>
/// </summary>
public sealed class AnalyzerVersionIsolationTests
{
	[Test]
	public void Resolves_a_dependency_to_the_version_beside_the_analyzer_that_wants_it()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-analyzer-versions-");

		try
		{
			var first = StageAnalyzer(root.FullName, "one", "1.0.0.0");
			var second = StageAnalyzer(root.FullName, "two", "2.0.0.0");

			using var loader = new ShadowCopyAnalyzerAssemblyLoader(
				NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance);

			// Every file in both directories, which is what MSBuild hands over. The second Support
			// overwrites the first in any map keyed on the simple name alone, and that is the bug:
			// after this, one of the two analyzers is asking for a version nothing will return.
			foreach (var path in new[] { first.Support, first.Analyzer, second.Support, second.Analyzer })
			{
				loader.AddDependencyLocation(path);
			}

			var one = loader.LoadFromPath(first.Analyzer);
			var two = loader.LoadFromPath(second.Analyzer);

			// Loading the analyzer is not the discriminator -- two identities can sit in one context
			// perfectly well. Calling through to the dependency is, because that is what makes the
			// runtime resolve Support by name and version.
			Assert.Equal("1.0.0.0", Call(one));
			Assert.Equal("2.0.0.0", Call(two));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	private static string Call(Assembly analyzer) =>
		(string)analyzer.GetType("Staged.Entry")!.GetMethod("SupportVersion")!.Invoke(null, null)!;

	/// <summary>
	/// Writes an analyzer and the dependency it calls into a directory of their own, both stamped
	/// with <paramref name="version"/>. Same two simple names every time; only the identities differ.
	/// </summary>
	private static (string Analyzer, string Support) StageAnalyzer(string root, string folder, string version)
	{
		var directory = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;

		var support = Path.Combine(directory, "Staged.Support.dll");
		Emit(
			"Staged.Support",
			version,
			$$"""
			[assembly: System.Reflection.AssemblyVersion("{{version}}")]

			namespace Staged;

			public static class Support
			{
				public static string Version() => "{{version}}";
			}
			""",
			support,
			references: []);

		var analyzer = Path.Combine(directory, "Staged.Analyzer.dll");
		Emit(
			"Staged.Analyzer",
			version,
			$$"""
			[assembly: System.Reflection.AssemblyVersion("{{version}}")]

			namespace Staged;

			public static class Entry
			{
				public static string SupportVersion() => Support.Version();
			}
			""",
			analyzer,
			references: [MetadataReference.CreateFromFile(support)]);

		return (analyzer, support);
	}

	private static void Emit(
		string assemblyName,
		string version,
		string source,
		string path,
		IReadOnlyList<MetadataReference> references)
	{
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			[.. Platform, .. references],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		EmitResult result = compilation.Emit(path);

		Assert.True(
			result.Success,
			$"Could not emit {assemblyName} {version}: {string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))}");
	}

	/// <summary>
	/// The runtime assemblies a two-line class needs. Taken from the host's own trusted list so the
	/// emitted code targets the runtime that will load it.
	/// </summary>
	private static IEnumerable<MetadataReference> Platform =>
		((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
			.Select(path => MetadataReference.CreateFromFile(path));
}
