using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Settings;

/// <summary>
/// Reads and writes <see cref="RoseSettings"/> at
/// <c>%LOCALAPPDATA%/BinaryVibrance/RoseMCP/settings.json</c>.
/// <para>
/// Beside the install and the logs, in the vendor/product folder they already share, because a
/// preference outlives an install and must not go with one: promoting a build replaces the tree
/// under that folder and a setting written inside it would be lost every deploy.
/// </para>
/// <para>
/// Nothing here throws for a file that is missing, unreadable or nonsense. A preference file is not
/// load-bearing -- every value in it has a default that works -- and a broker that refused to start
/// because somebody hand-edited a JSON file into an invalid state would be trading a small
/// inconvenience for a large one.
/// </para>
/// </summary>
public static class RoseSettingsFile
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
	};

	/// <summary>The file, which need not exist.</summary>
	/// <param name="localAppData">Where local application data is, for a test that wants its own.</param>
	public static string PathFor(string? localAppData = null)
	{
		var configured = localAppData is { Length: > 0 }
			? localAppData
			: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		var root = configured.Length > 0 ? configured : System.IO.Path.GetTempPath();

		return System.IO.Path.Combine(root, "BinaryVibrance", "RoseMCP", "settings.json");
	}

	/// <summary>
	/// What has been chosen, or the defaults. A file that cannot be read is the defaults too: the
	/// alternative is a host that will not start over a preference.
	/// </summary>
	public static RoseSettings Read(string? localAppData = null)
	{
		try
		{
			var path = PathFor(localAppData);
			if (!File.Exists(path)) return new RoseSettings();

			return JsonSerializer.Deserialize<RoseSettings>(File.ReadAllText(path), Options) ?? new RoseSettings();
		}
		catch (Exception)
		{
			return new RoseSettings();
		}
	}

	/// <summary>
	/// Writes the whole file, reporting whether it landed. False rather than an exception, so a
	/// toggle in a window can say it did not stick instead of taking the window down with it.
	/// </summary>
	public static bool Write(RoseSettings settings, string? localAppData = null)
	{
		try
		{
			var path = PathFor(localAppData);
			var directory = System.IO.Path.GetDirectoryName(path);

			if (directory is not null) Directory.CreateDirectory(directory);

			File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
