using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Worker;

/// <summary>
/// A <c>rosemcp.json</c> beside the solution, pinning the MSBuild properties this repository wants
/// to be loaded under.
/// <para>
/// It exists because of the rule that no tool needs a setup call first. Auto-selection can pick a
/// configuration the solution declares, but it cannot know that this repository's four Revit
/// configurations are not interchangeable, and an agent that has to be told to reload before its
/// first useful answer has already lost to grep. Committing the answer once removes the question.
/// </para>
/// <para>
/// Found by walking up from the solution, as MSBuild and NuGet find theirs, so a solution in a
/// subdirectory is covered by one file at the repository root.
/// </para>
/// </summary>
public sealed record WorkspaceConfigFile
{
	public const string FileName = "rosemcp.json";

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
	/// The nearest file at or above the solution, or null when there is none. A file that cannot be
	/// read or parsed is treated as absent rather than fatal: a malformed one should not stop a
	/// workspace opening, and the load reports which file it used so silence is visible.
	/// </summary>
	public static WorkspaceConfigFile? Find(string solutionPath)
	{
		var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath));

		while (!string.IsNullOrEmpty(directory))
		{
			var candidate = System.IO.Path.Combine(directory, FileName);
			if (File.Exists(candidate)) return Read(candidate);

			directory = System.IO.Path.GetDirectoryName(directory);
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
