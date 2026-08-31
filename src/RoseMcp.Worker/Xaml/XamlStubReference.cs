using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Hands a generator to Roslyn without there being an assembly to load.
/// <para>
/// AnalyzerReference is public and its GetGenerators is virtual, which is what makes the whole
/// approach light: no analyzer DLL to ship, no version to match against the host's Roslyn, and
/// nothing shadow-copied. FullPath stays null because there is no file, and Id is the instance,
/// which is unique per project exactly as the reference is.
/// </para>
/// </summary>
public sealed class XamlStubReference(ISourceGenerator generator) : AnalyzerReference
{
	public override string? FullPath => null;

	public override string Display => "RoseMCP XAML stubs";

	public override object Id => this;

	public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

	public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];

	public override ImmutableArray<ISourceGenerator> GetGenerators(string language) =>
		language == LanguageNames.CSharp ? [generator] : [];

	public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [generator];
}
