using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoslynMcp.Worker;

/// <summary>
/// Reads the project list straight out of a solution file, without MSBuild.
/// <para>
/// This exists to break a chicken-and-egg problem: deciding whether a restore is needed requires
/// knowing the projects, but loading the projects requires a design-time build, which requires
/// restore. Both solution formats are trivial to parse, so we do that instead.
/// </para>
/// </summary>
public static partial class SolutionFileReader
{
	private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

	/// <summary>
	/// Absolute paths of every project in <paramref name="solutionPath"/>, which may itself be a
	/// bare project file. Solution folders and unrecognised project types are skipped.
	/// </summary>
	public static IReadOnlyList<string> ReadProjectPaths(string solutionPath)
	{
		var extension = Path.GetExtension(solutionPath);
		if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
			return [Path.GetFullPath(solutionPath)];

		var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? ".";

		var relativePaths = extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
			? ReadSlnx(solutionPath)
			: ReadClassicSln(solutionPath);

		return relativePaths
			.Where(path => ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
			.Select(path => Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar))))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static IEnumerable<string> ReadSlnx(string solutionPath)
	{
		// Projects may sit at the root or nested inside <Folder> elements, so match by name anywhere.
		return XDocument.Load(solutionPath)
			.Descendants()
			.Where(element => element.Name.LocalName == "Project")
			.Select(element => (string?)element.Attribute("Path"))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(path => path!);
	}

	private static IEnumerable<string> ReadClassicSln(string solutionPath)
	{
		foreach (var line in File.ReadLines(solutionPath))
		{
			var match = ClassicProjectLine().Match(line);
			if (match.Success)
				yield return match.Groups["path"].Value;
		}
	}

	// Project("{type-guid}") = "Name", "relative\path.csproj", "{project-guid}"
	[GeneratedRegex("^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+)\"", RegexOptions.CultureInvariant)]
	private static partial Regex ClassicProjectLine();
}
