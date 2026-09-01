using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RoseMcp.Worker;

/// <summary>
/// The code fixes a project's analyzers already bring with them.
/// <para>
/// Analyzers ship their fixers in the same assemblies, so a solution that reports a diagnostic
/// usually also carries the code that repairs it -- measured at 206 fix providers over 186 diagnostic
/// ids for this repository, and 143 over 120 for a Revit add-in, with no package added to either.
/// Roslyn exposes the analyzers through <see cref="AnalyzerReference"/> but not the fixers, so those
/// are found by reflection over the same assembly.
/// </para>
/// <para>
/// Loaded through <see cref="ShadowCopyAnalyzerAssemblyLoader"/> and never from
/// <see cref="AnalyzerReference.FullPath"/> directly. That path is deliberately still the original --
/// callers check it against disk -- so loading from it would hold the user's own analyzer open for
/// the life of the worker, which is the failure the shadow copies exist to prevent.
/// </para>
/// </summary>
public sealed class CodeFixCatalog(ShadowCopyAnalyzerAssemblyLoader loader, ILogger<CodeFixCatalog> logger)
{
	private readonly ConcurrentDictionary<string, ImmutableArray<CodeFixProvider>> _byAssembly =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Fix providers in this project's analyzers that offer a fix for <paramref name="diagnosticId"/>.</summary>
	public ImmutableArray<CodeFixProvider> ProvidersFor(Project project, string diagnosticId) =>
		[.. Providers(project).Where(provider =>
			provider.FixableDiagnosticIds.Contains(diagnosticId, StringComparer.Ordinal))];

	/// <summary>
	/// Analyzers that report <paramref name="diagnosticId"/>, and only those.
	/// <para>
	/// This is what makes fixing one rule affordable. A full analyzer pass over a large project is
	/// seconds to minutes; running the two analyzers that report the id being fixed is a fraction of
	/// it, and produces exactly the same diagnostics for that id.
	/// </para>
	/// </summary>
	public ImmutableArray<DiagnosticAnalyzer> AnalyzersFor(Project project, string diagnosticId) =>
		[.. project.AnalyzerReferences
			.SelectMany(reference => SafeAnalyzers(reference, project.Language))
			.Where(analyzer => analyzer.SupportedDiagnostics
				.Any(descriptor => string.Equals(descriptor.Id, diagnosticId, StringComparison.Ordinal)))];

	/// <summary>Every diagnostic id this project has a fix for, sorted, for reporting.</summary>
	public IReadOnlyList<string> FixableIds(Project project) =>
		[.. Providers(project)
			.SelectMany(provider => provider.FixableDiagnosticIds)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];

	private IEnumerable<CodeFixProvider> Providers(Project project) =>
		project.AnalyzerReferences
			.Select(reference => reference.FullPath)
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.SelectMany(path => FromAssembly(path).AsEnumerable());

	/// <summary>
	/// Cached per assembly path, because this reflects over every exported type and a solution has
	/// dozens of analyzer assemblies shared across its projects.
	/// </summary>
	private ImmutableArray<CodeFixProvider> FromAssembly(string path) =>
		_byAssembly.GetOrAdd(path, Discover);

	private ImmutableArray<CodeFixProvider> Discover(string path)
	{
		Type[] types;
		try
		{
			types = loader.LoadFromPath(path).GetTypes();
		}
		catch (Exception exception) when (exception is BadImageFormatException or FileLoadException
			or FileNotFoundException or ReflectionTypeLoadException or NotSupportedException)
		{
			logger.LogDebug("No code fixes read from {Path}: {Message}", path, exception.Message);

			return [];
		}

		var providers = ImmutableArray.CreateBuilder<CodeFixProvider>();

		foreach (var type in types)
		{
			if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type)) continue;

			try
			{
				// A provider that will not construct outside its host is one we could not have run.
				if (Activator.CreateInstance(type) is CodeFixProvider provider) providers.Add(provider);
			}
			catch (Exception exception) when (exception is MissingMethodException or TargetInvocationException
				or TypeLoadException or NotSupportedException)
			{
				logger.LogDebug("{Type} would not construct: {Message}", type.FullName, exception.Message);
			}
		}

		return providers.ToImmutable();
	}

	/// <summary>
	/// An analyzer assembly that fails to load reports through an event and then returns nothing, so
	/// the throw has to be caught here rather than left to surface as a rule that never fires.
	/// </summary>
	private static ImmutableArray<DiagnosticAnalyzer> SafeAnalyzers(AnalyzerReference reference, string language)
	{
		try
		{
			return reference.GetAnalyzers(language);
		}
		catch (Exception exception) when (exception is BadImageFormatException or FileLoadException
			or FileNotFoundException or ReflectionTypeLoadException)
		{
			return [];
		}
	}
}
