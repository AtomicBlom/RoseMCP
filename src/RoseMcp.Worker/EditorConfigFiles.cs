using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// What the .editorconfig files on disk say about a path, for a file whose project was never given them.
/// <para>
/// Roslyn reads .editorconfig through the project: the design-time build lists the files covering the
/// project's sources, and a project the workspace has not built that way -- one added to the solution
/// after the workspace loaded, or one whose build stopped before it got that far -- has none. Every
/// question about its files is then answered with Roslyn's defaults, which in a repository that says
/// otherwise is a file written with the wrong indentation and endings by the tool whose job is to get
/// them right, and certified as formatted by the tool that checks. The files are on disk the whole time,
/// and reading one is a parse rather than a build.
/// </para>
/// </summary>
internal static class EditorConfigFiles
{
	private const string FileName = ".editorconfig";

	/// <summary>
	/// The options the .editorconfig files from the path's directory up to the root of its drive give it,
	/// combined the way the compiler combines them: a nearer file wins, and one saying
	/// <c>root = true</c> hides everything above it.
	/// </summary>
	internal static ImmutableDictionary<string, string> For(string path)
	{
		var full = Path.GetFullPath(path);
		var start = Path.GetDirectoryName(full);
		var configs = new List<AnalyzerConfig>();

		for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
		{
			var file = Path.Combine(directory.FullName, FileName);
			if (!File.Exists(file)) continue;

			try
			{
				configs.Add(AnalyzerConfig.Parse(File.ReadAllText(file), file));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// A file that cannot be read says nothing, as it says nothing to the build either.
			}
		}

		return configs.Count == 0
			? ImmutableDictionary<string, string>.Empty
			: AnalyzerConfigSet.Create(configs).GetOptionsForSourcePath(full).AnalyzerOptions;
	}

	/// <summary>
	/// Whether <paramref name="project"/> was given an .editorconfig covering <paramref name="path"/>, in
	/// which case Roslyn's reading of it is the answer and the disk has nothing to add.
	/// </summary>
	internal static bool Given(Project project, string path) =>
		project.AnalyzerConfigDocuments.Any(config => config.FilePath is { } file
			&& Path.GetFileName(file).Equals(FileName, StringComparison.OrdinalIgnoreCase)
			&& Path.GetDirectoryName(file) is { } directory
			&& path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
