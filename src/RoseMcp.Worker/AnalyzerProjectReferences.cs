using System.Xml.Linq;

namespace RoseMcp.Worker;

/// <summary>
/// Finds ProjectReference items carrying OutputItemType="Analyzer" -- the in-solution source
/// generator pattern -- and maps each to the project that is supposed to produce it.
/// <para>
/// This has to come from project XML because the loaded model cannot answer it. When such a
/// generator has never been built, MSBuild still emits its expected output path on the compiler
/// /analyzer: line, so Roslyn dutifully creates a reference pointing at a file that is not there.
/// The workspace then looks like a project whose generators simply produce nothing.
/// </para>
/// </summary>
public static class AnalyzerProjectReferences
{
	/// <summary>MSBuild writes Windows separators into project files whatever the host OS.</summary>
	private const char ProjectFileSeparator = '\\';

	/// <summary>
	/// Maps the expected output assembly name of each analyzer-typed project reference to the path
	/// of the project that builds it, so a missing analyzer can name the project to build.
	/// </summary>
	public static IReadOnlyDictionary<string, string> ReadAnalyzerProjects(string projectPath)
	{
		var document = TryLoad(projectPath);
		if (document is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
		{
			var outputItemType = (string?)reference.Attribute("OutputItemType")
				?? reference.Elements().FirstOrDefault(child => child.Name.LocalName == "OutputItemType")?.Value;

			if (!string.Equals(outputItemType?.Trim(), "Analyzer", StringComparison.OrdinalIgnoreCase)) continue;

			var include = (string?)reference.Attribute("Include");
			if (string.IsNullOrWhiteSpace(include)) continue;

			var normalised = include.Replace(ProjectFileSeparator, Path.DirectorySeparatorChar);
			var referenced = Path.GetFullPath(Path.Combine(directory, normalised));
			map[ReadAssemblyName(referenced)] = referenced;
		}

		return map;
	}

	/// <summary>
	/// The output assembly name, which is what appears on the analyzer list. Honours an explicit
	/// AssemblyName; otherwise MSBuild defaults it to the project file name.
	/// </summary>
	private static string ReadAssemblyName(string projectPath)
	{
		var fallback = Path.GetFileNameWithoutExtension(projectPath);
		var declared = TryLoad(projectPath)
			?.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value;

		return string.IsNullOrWhiteSpace(declared) ? fallback : declared.Trim();
	}

	private static XDocument? TryLoad(string projectPath)
	{
		try
		{
			return XDocument.Load(projectPath);
		}
		catch (Exception exception) when (exception is IOException or System.Xml.XmlException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
