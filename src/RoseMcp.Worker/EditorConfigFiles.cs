using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// The analyzer config files on disk -- .editorconfig and .globalconfig -- set against the ones Roslyn was given
/// for a project.
/// <para>
/// Roslyn reads them through the project: the design-time build lists the files in every directory above a
/// source it compiles, and a project the workspace has not built that way -- one with nothing to compile when it
/// was built, or one whose build stopped before it got that far -- has none, as has a folder that held no source
/// then. Every question about its files is then answered with Roslyn's defaults, which in a repository that says
/// otherwise is a file written with the wrong indentation and endings by the tool whose job is to get them right,
/// certified as formatted by the tool that checks, and compiled clean under severities the build does not use.
/// The files are on disk the whole time, and reading one is a parse rather than a build.
/// </para>
/// </summary>
public static class EditorConfigFiles
{
	private const string FileName = ".editorconfig";

	private const string GlobalFileName = ".globalconfig";

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
	/// The analyzer config files on disk a design-time build gives <paramref name="project"/> once it has a source
	/// in its own directory, and that it was not given: an .editorconfig or a .globalconfig, as
	/// <paramref name="discovery"/> says it reads them, in its directory and every one above it.
	/// <para>
	/// The build lists the directories above each file it compiles, so a project with nothing to compile when it
	/// was built -- one added to the solution before any source was written into it -- is given none of them,
	/// while the same project built a minute later, with a file in it, is given all of them. Everything it then
	/// compiles comes from inside its directory, so these are exactly what that later build would add.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> MissingAbove(Project project, ConfigDiscovery discovery)
	{
		if (project.FilePath is not { Length: > 0 } projectFile) return [];
		if (Path.GetDirectoryName(Path.GetFullPath(projectFile)) is not { } directory) return [];

		var given = GivenPaths(project);

		return [.. AtOrAbove(directory, discovery).Where(path => !given.Contains(path))];
	}

	/// <summary>
	/// The analyzer config files on disk that apply to <paramref name="path"/> and that <paramref name="project"/>
	/// was not given, so whatever they set -- a severity raised to an error, an indentation -- is missing from
	/// every answer the workspace gives about the file. Empty where the project was given them all, and where
	/// nothing on disk speaks for the file.
	/// <para>
	/// An .editorconfig applies from the file's own directory up to the first one saying <c>root = true</c>, and
	/// one above that changes nothing. A .globalconfig applies to the whole project wherever it sits, so every one
	/// above the file counts.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> NotGiven(Project project, string path)
	{
		if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory) return [];

		var given = GivenPaths(project);
		var missing = new List<string>();
		var rooted = false;

		foreach (var file in AtOrAbove(directory, ConfigDiscovery.Both))
		{
			var isEditorConfig = IsEditorConfig(file);
			if (isEditorConfig && rooted) continue;

			if (!given.Contains(file)) missing.Add(file);
			if (isEditorConfig) rooted = IsRoot(file);
		}

		return missing;
	}

	/// <summary>Whether <paramref name="path"/> names an .editorconfig rather than a .globalconfig.</summary>
	public static bool IsEditorConfig(string path) =>
		Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase);

	/// <summary>The analyzer config files of the kinds named, from <paramref name="directory"/> up to the root of its drive, nearest first.</summary>
	private static IEnumerable<string> AtOrAbove(string directory, ConfigDiscovery discovery)
	{
		for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
		{
			if (discovery.HasFlag(ConfigDiscovery.EditorConfig) && Existing(current, FileName) is { } editorConfig) yield return editorConfig;
			if (discovery.HasFlag(ConfigDiscovery.GlobalConfig) && Existing(current, GlobalFileName) is { } globalConfig) yield return globalConfig;
		}
	}

	private static string? Existing(DirectoryInfo directory, string name)
	{
		var path = Path.Combine(directory.FullName, name);

		return File.Exists(path) ? path : null;
	}

	/// <summary>
	/// Whether an .editorconfig says <c>root = true</c> in its preamble, before any section, which is the only
	/// place the key means anything. Read here rather than asked of Roslyn's parse, which keeps the answer to
	/// itself. One that cannot be read says nothing, as it says nothing to the build either.
	/// </summary>
	private static bool IsRoot(string path)
	{
		try
		{
			foreach (var line in File.ReadLines(path))
			{
				var text = line.Trim();
				if (text.StartsWith('[')) return false;

				var equals = text.IndexOf('=', StringComparison.Ordinal);
				if (equals < 0) continue;

				var isRootKey = text[..equals].Trim().Equals("root", StringComparison.OrdinalIgnoreCase);
				if (isRootKey) return text[(equals + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
			}

			return false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static HashSet<string> GivenPaths(Project project) =>
		project.AnalyzerConfigDocuments
			.Select(config => config.FilePath)
			.OfType<string>()
			.Select(Path.GetFullPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
