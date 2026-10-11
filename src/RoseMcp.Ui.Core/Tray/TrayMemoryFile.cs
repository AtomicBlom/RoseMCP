using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// Reads and writes <see cref="TrayMemory"/>. Like the settings file, nothing here throws: a memory
/// that cannot be read is an empty one, which costs at most a notification said twice.
/// </summary>
public static class TrayMemoryFile
{
	/// <summary>The file's name, in the folder the settings file is in.</summary>
	public const string FileName = "tray.json";

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>The file in <paramref name="directory"/>, which need not exist.</summary>
	public static string PathIn(string directory) => Path.Combine(directory, FileName);

	/// <summary>What was remembered, or nothing where the file is missing or unreadable.</summary>
	public static TrayMemory Read(string path)
	{
		try
		{
			if (!File.Exists(path)) return new TrayMemory();

			return JsonSerializer.Deserialize<TrayMemory>(File.ReadAllText(path), Options) ?? new TrayMemory();
		}
		catch (Exception)
		{
			return new TrayMemory();
		}
	}

	/// <summary>
	/// Writes the whole file, reporting whether it landed. Written beside the file and moved over it, so a
	/// tray killed mid-write -- which is how a deploy stops one -- leaves the old memory rather than half
	/// of a new one.
	/// </summary>
	public static bool Write(string path, TrayMemory memory)
	{
		var staged = path + ".tmp";

		try
		{
			if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

			File.WriteAllText(staged, JsonSerializer.Serialize(memory, Options));
			File.Move(staged, path, overwrite: true);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
