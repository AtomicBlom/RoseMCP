using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Whether markup no stub dialect was chosen for is compiled inside the workspace all the same, by a source generator of
/// its framework's own: any framework that turns its markup into partial classes as it compiles.
/// <para>
/// Asked here, of the finished compilation, because the stub generator cannot ask it: generators run side by side on the
/// same input and never see each other's output. The worker runs them all to report on them, and so can look.
/// </para>
/// <para>
/// The evidence is each class the markup names already having an <c>InitializeComponent</c> a generator wrote. That is
/// what a markup compiler adds and what code-behind calls, so where it is there the code-behind binds, and there are no
/// false errors to warn about. Every class has to have it: one file a generator left alone is markup nothing compiles,
/// and the warning about it stands.
/// </para>
/// </summary>
public static class XamlCompiledElsewhere
{
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>
	/// The name of the generator assembly that compiles every class in <paramref name="classes"/>, or null when any of them
	/// has no <c>InitializeComponent</c> declared in a generated file, or there are none.
	/// </summary>
	/// <param name="compilation">The project's compilation with every generator's output in it.</param>
	/// <param name="generatedFiles">
	/// The paths of the project's source-generated documents, which Roslyn places at generator assembly, generator type,
	/// hint name.
	/// </param>
	/// <param name="classes">The classes the markup names with <c>x:Class</c>.</param>
	public static string? Find(Compilation compilation, IReadOnlyCollection<string> generatedFiles, IReadOnlyList<string> classes)
	{
		if (classes.Count == 0) return null;

		var generated = new HashSet<string>(generatedFiles, PathComparer);
		string? compiler = null;

		foreach (var name in classes)
		{
			var dot = name.LastIndexOf('.');
			string[] space = dot < 0 ? [] : name[..dot].Split('.');

			var file = SymbolResolver.TypesIn(compilation, space, name[(dot + 1)..])
				.SelectMany(type => type.GetMembers("InitializeComponent"))
				.SelectMany(member => member.DeclaringSyntaxReferences)
				.Select(reference => reference.SyntaxTree.FilePath)
				.FirstOrDefault(generated.Contains);

			if (file is null) return null;

			compiler ??= GeneratorAssemblyOf(file);
		}

		return compiler;
	}

	/// <summary>The generator assembly a generated document's path names, two folders above the file.</summary>
	private static string GeneratorAssemblyOf(string path) =>
		Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) is { Length: > 0 } assembly
			? assembly
			: Path.GetFileName(path);
}
