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

	public static IReadOnlyList<string> Read(string projectFilePath)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
		if (directory is null) return [];

		var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		ReadProjectItems(projectFilePath, directory, found, removed);
		Glob(directory, found);

		found.ExceptWith(removed);

		return [.. found.Where(File.Exists)];
	}

	/// <summary>
	/// Explicit Page, ApplicationDefinition and Resource items, plus any Remove entries, which is how
	/// an SDK-style project opts a file out of the default glob.
	/// </summary>
	private static void ReadProjectItems(
		string projectFilePath,
		string directory,
		SortedSet<string> found,
		HashSet<string> removed)
	{
		XDocument project;
		try
		{
			project = XDocument.Load(projectFilePath);
		}
		catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
		{
			return;
		}

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
	/// What the SDK's default items would have picked up. Wildcards in explicit items are left to
	/// this too, rather than reimplementing MSBuild globbing.
	/// </summary>
	private static void Glob(string directory, SortedSet<string> found)
	{
		foreach (var path in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories))
		{
			if (IsUnderBuildOutput(path, directory)) continue;

			found.Add(Path.GetFullPath(path));
		}
	}

	private static bool IsUnderBuildOutput(string path, string directory)
	{
		var relative = Path.GetRelativePath(directory, path);

		return relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
