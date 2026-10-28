using System.Xml.Linq;

using RoseMcp.Solutions;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Finds a project's XAML files.
/// <para>
/// The design-time build does not tell us: it reports no XAML items and no additional files at all,
/// which is the whole reason this feature exists. So the project file is read directly for explicit
/// items -- legacy UWP and WPF projects list every Page by hand -- and the project directory is
/// globbed for the SDK-style case, where the items come from a default glob nobody writes down.
/// Reading the project file rather than evaluating it follows the same pragmatism as
/// <see cref="SolutionFileReader"/>.
/// </para>
/// </summary>
public static class XamlItemReader
{
	private static readonly string[] ItemNames = ["Page", "ApplicationDefinition", "Resource"];

	/// <summary>The SDK's build output folders, which its own excludes name by property and so nothing here can read.</summary>
	private static readonly string[] BuildOutput = ["bin", "obj"];

	/// <summary>MSBuild globs ignore case on Windows and nowhere else.</summary>
	private static readonly StringComparison PathComparison =
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	public static IReadOnlyList<string> Read(string projectFilePath)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
		if (directory is null) return [];

		var project = Load(projectFilePath);
		var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (project is not null) ReadProjectItems(project, directory, found, removed);
		Glob(directory, directory, [.. BuildOutput, .. ExcludedFolders(project)], found);

		found.ExceptWith(removed);

		return [.. found.Where(File.Exists)];
	}

	/// <summary>
	/// Explicit Page, ApplicationDefinition and Resource items, plus any Remove entries, which is how
	/// an SDK-style project opts a file out of the default glob.
	/// </summary>
	private static void ReadProjectItems(
		XDocument project,
		string directory,
		SortedSet<string> found,
		HashSet<string> removed)
	{
		foreach (var element in project.Descendants().Where(element => ItemNames.Contains(element.Name.LocalName)))
		{
			foreach (var attribute in (string[])["Include", "Update"])
			{
				foreach (var path in Split((string?)element.Attribute(attribute)))
				{
					if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) found.Add(Resolve(directory, path));
				}
			}

			// A conditioned Remove is not honoured, because whether it applies depends on properties
			// nothing here evaluates. WPF projects really do this: an App.xaml removed from
			// ApplicationDefinition in Release only, so honouring it unconditionally loses the entry
			// point in Debug. Keeping a file we should have dropped costs a stub nobody uses; dropping
			// one we should have kept costs the errors this feature exists to remove.
			if (IsConditional(element)) continue;

			foreach (var path in Split((string?)element.Attribute("Remove")))
			{
				if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) removed.Add(Resolve(directory, path));
			}
		}
	}

	/// <summary>
	/// What the SDK's default items would have picked up: everything under the project's folder except build output, the
	/// folders the project excludes by name, and every folder whose name starts with a dot. That last is the SDK's own
	/// <c>**/.*/**</c>, and it matters here more than anywhere: an agent's worktrees sit under <c>.claude</c> and each holds
	/// a whole copy of the repository, so markup belonging to every other project in it. An excluded folder is not walked
	/// at all. Wildcards in explicit items are left to this too, rather than reimplementing MSBuild globbing.
	/// </summary>
	private static void Glob(string projectDirectory, string directory, IReadOnlyList<string> excluded, SortedSet<string> found)
	{
		foreach (var path in Directory.EnumerateFiles(directory, "*.xaml")) found.Add(Path.GetFullPath(path));

		foreach (var folder in Directory.EnumerateDirectories(directory))
		{
			if (IsExcluded(projectDirectory, folder, excluded)) continue;

			Glob(projectDirectory, folder, excluded, found);
		}
	}

	private static bool IsExcluded(string projectDirectory, string folder, IReadOnlyList<string> excluded)
	{
		if (Path.GetFileName(folder).StartsWith('.')) return true;

		var relative = Path.GetRelativePath(projectDirectory, folder).Replace('\\', '/');

		return excluded.Any(name => relative.Equals(name, PathComparison) || relative.StartsWith(name + "/", PathComparison));
	}

	/// <summary>
	/// The folders the project's own <c>DefaultItemExcludes</c> keeps out of its default items, relative to the project
	/// and with forward slashes: <c>External/**</c> is how a project keeps a submodule's files out of its own. Only a pattern
	/// naming a folder outright is honoured, and only where no condition decides whether it applies, for the same reason
	/// a conditioned Remove is not.
	/// </summary>
	private static IEnumerable<string> ExcludedFolders(XDocument? project) => project is null
		? []
		: project.Descendants()
			.Where(element => element.Name.LocalName == "DefaultItemExcludes" && !IsConditional(element))
			.SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.Select(FolderOf)
			.OfType<string>();

	/// <summary>The folder a <c>Folder/**</c> pattern names, or null for a pattern with a wildcard or a property anywhere else.</summary>
	private static string? FolderOf(string pattern)
	{
		var path = pattern.Replace('\\', '/');
		if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];

		foreach (var suffix in (string[])["/**/*", "/**"])
		{
			if (!path.EndsWith(suffix, StringComparison.Ordinal)) continue;

			var folder = path[..^suffix.Length];
			var isLiteral = folder.Length > 0 && folder.IndexOfAny(['*', '?', '$', '%', '@']) < 0;

			return isLiteral ? folder : null;
		}

		return null;
	}

	private static XDocument? Load(string projectFilePath)
	{
		try
		{
			return XDocument.Load(projectFilePath);
		}
		catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
		{
			return null;
		}
	}

	/// <summary>Whether this item or any group above it carries a Condition.</summary>
	private static bool IsConditional(XElement element) =>
		element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute("Condition") is not null);

	private static IEnumerable<string> Split(string? value) => value is null or ""
		? []
		: value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(part => !part.Contains('*', StringComparison.Ordinal));

	private static string Resolve(string directory, string path) =>
		Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));
}
