using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RoseMcp.Worker;

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
		if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return [Path.GetFullPath(solutionPath)];

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

	/// <summary>
	/// The configuration and platform names the solution declares, or <see cref="SolutionConfigurations.None"/>
	/// when it declares none. Parsed rather than evaluated for the same reason the project list is:
	/// the answer is needed before any build can run, because it decides what to build with.
	/// </summary>
	public static SolutionConfigurations ReadConfigurations(string solutionPath)
	{
		try
		{
			var extension = Path.GetExtension(solutionPath);

			if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
			{
				return ReadProjectConfigurations(solutionPath);
			}

			return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
				? ReadSlnxConfigurations(solutionPath)
				: ReadClassicConfigurations(solutionPath);
		}
		catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
		{
			return SolutionConfigurations.None;
		}
	}

	/// <summary>
	/// A .slnx keeps them under a single Configurations element. Only entries carrying a Name are
	/// declarations; the BuildType and Platform elements that appear under a Project are mappings from
	/// a solution configuration to a project one, and naming those would invent configurations the
	/// solution does not offer.
	/// </summary>
	private static SolutionConfigurations ReadSlnxConfigurations(string solutionPath)
	{
		var declarations = XDocument.Load(solutionPath)
			.Descendants()
			.FirstOrDefault(element => element.Name.LocalName == "Configurations");

		if (declarations is null) return SolutionConfigurations.None;

		return new SolutionConfigurations
		{
			Configurations = Names(declarations, "BuildType"),
			Platforms = [.. Names(declarations, "Platform").Select(Platform)],
		};
	}

	private static string[] Names(XElement declarations, string elementName) =>
		[.. declarations.Elements()
			.Where(element => element.Name.LocalName == elementName)
			.Select(element => (string?)element.Attribute("Name"))
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Select(name => name!.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// A classic .sln lists them as "Debug|x64 = Debug|x64" inside SolutionConfigurationPlatforms.
	/// The section is read rather than the whole file scanned, because the same "Config|Platform"
	/// shape appears again in ProjectConfigurationPlatforms with project GUIDs attached.
	/// </summary>
	private static SolutionConfigurations ReadClassicConfigurations(string solutionPath)
	{
		var configurations = new List<string>();
		var platforms = new List<string>();
		var inSection = false;

		foreach (var line in File.ReadLines(solutionPath))
		{
			var trimmed = line.Trim();

			if (trimmed.StartsWith("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.Ordinal))
			{
				inSection = true;
				continue;
			}

			if (!inSection) continue;
			if (trimmed.StartsWith("EndGlobalSection", StringComparison.Ordinal)) break;

			var name = trimmed.Split('=')[0].Trim();
			var separator = name.IndexOf('|', StringComparison.Ordinal);
			if (separator <= 0) continue;

			Add(configurations, name[..separator]);
			Add(platforms, Platform(name[(separator + 1)..]));
		}

		return new SolutionConfigurations { Configurations = configurations, Platforms = platforms };
	}

	/// <summary>
	/// A bare project file declares its own, and both properties are plain semicolon lists. Only what
	/// the file itself says is visible here -- a Directory.Build.props further up is not read, since
	/// resolving imports is evaluation, which is the thing this class exists to avoid.
	/// </summary>
	private static SolutionConfigurations ReadProjectConfigurations(string projectPath)
	{
		var properties = XDocument.Load(projectPath).Descendants().ToArray();

		return new SolutionConfigurations
		{
			Configurations = List(properties, "Configurations"),
			Platforms = [.. List(properties, "Platforms").Select(Platform)],
		};
	}

	private static string[] List(IReadOnlyList<XElement> properties, string propertyName) =>
		[.. properties
			.Where(element => element.Name.LocalName == propertyName)
			.SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// A platform as MSBuild spells it. Solution files write "Any CPU" with a space and the property
	/// is <c>AnyCPU</c> without one -- a solution build maps between them, and passing the solution's
	/// spelling to a project as a global property instead moves every output path to bin\Any CPU\.
	/// </summary>
	private static string Platform(string name) =>
		name.Trim().Equals("Any CPU", StringComparison.OrdinalIgnoreCase) ? "AnyCPU" : name.Trim();

	private static void Add(List<string> names, string candidate)
	{
		var name = candidate.Trim();
		if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
	}
}
