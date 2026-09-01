using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Worker;

/// <summary>
/// A file beside one solution, pinning the MSBuild properties that solution wants to be loaded
/// under: <c>A.slnx.rosemcp.json</c> for A specifically, or a plain <c>rosemcp.json</c> as the
/// default for its directory.
/// <para>
/// It exists because of the rule that no tool needs a setup call first. Auto-selection can pick a
/// configuration the solution declares, but it cannot know that a repository's four Revit
/// configurations are not interchangeable, and an agent that has to be told to reload before its
/// first useful answer has already lost to grep. Committing the answer once removes the question.
/// </para>
/// <para>
/// Scoped to one solution rather than to a repository, and deliberately not found by walking up.
/// Configurations are a property of a solution, not of a tree: Drawboard's Revit repository holds
/// <c>Db.Revit.slnx</c>, which declares Debug-2024 through Debug-2027 and nothing else, beside
/// <c>Db.Revit.Installer.slnx</c>, which declares no build types at all -- so a file anywhere above
/// them would be wrong for one of them. Naming a file after its solution is what
/// <c>Db.Revit.slnx.DotSettings</c> in that same directory already does.
/// </para>
/// </summary>
public sealed record WorkspaceConfigFile
{
	public const string FileName = "rosemcp.json";

	/// <summary>The name of the file that pins one named solution: <c>A.slnx.rosemcp.json</c>.</summary>
	public static string NameFor(string solutionPath) =>
		$"{System.IO.Path.GetFileName(solutionPath)}.{FileName}";

	private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	/// <summary>Where it was found, so a caller told what was pinned can also be told by whom.</summary>
	[JsonIgnore]
	public string Path { get; init; } = string.Empty;

	public string? Configuration { get; init; }

	public string? Platform { get; init; }

	/// <summary>
	/// Any other MSBuild global properties. Needed where the target framework is chosen by neither
	/// configuration nor platform.
	/// </summary>
	public Dictionary<string, string> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// This solution's file, or its directory's, or null when there is neither. The solution-specific
	/// name wins, so one directory can hold two solutions that disagree.
	/// <para>
	/// A file that cannot be read or parsed is treated as absent rather than fatal: a malformed one
	/// should not stop a workspace opening, and the load reports which file it used, so a file being
	/// ignored is visible rather than silent.
	/// </para>
	/// </summary>
	public static WorkspaceConfigFile? Find(string solutionPath)
	{
		var full = System.IO.Path.GetFullPath(solutionPath);
		var directory = System.IO.Path.GetDirectoryName(full);
		if (string.IsNullOrEmpty(directory)) return null;

		foreach (var name in (string[])[NameFor(full), FileName])
		{
			var candidate = System.IO.Path.Combine(directory, name);
			if (File.Exists(candidate)) return Read(candidate);
		}

		return null;
	}

	private static WorkspaceConfigFile? Read(string path)
	{
		try
		{
			var parsed = JsonSerializer.Deserialize<WorkspaceConfigFile>(File.ReadAllText(path), ReadOptions);

			return parsed is null ? null : parsed with { Path = path };
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
