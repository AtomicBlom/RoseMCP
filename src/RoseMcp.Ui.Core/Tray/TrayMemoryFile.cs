using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// Reads and changes <see cref="TrayMemory"/>. Like the settings file, nothing here throws: a memory that
/// cannot be read is an empty one, which costs at most a notification said twice.
/// <para>
/// More than one tray can share the file -- an installed one beside one run from a source tree -- so it is
/// never written back from a copy read earlier. Each change reads the file as it is now, changes the one field
/// it is about, and writes it, holding a lock file while it does so another copy's change in between is not
/// lost. A lock that cannot be had within a moment is done without: the memory is not worth waiting on.
/// </para>
/// </summary>
public static class TrayMemoryFile
{
	/// <summary>The file's name, in the folder the settings file is in.</summary>
	public const string FileName = "tray.json";

	/// <summary>How long a change waits for another copy's change to finish before it goes ahead regardless.</summary>
	private static readonly TimeSpan LockWithin = TimeSpan.FromSeconds(2);

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
	/// The memory to change, or false where the file is there and could not be read for a reason that passes --
	/// a scanner or another process holding it. Writing an empty memory back over that would lose every other
	/// install's last build and let a release be announced again. A file that reads but is not a memory is
	/// taken as an empty one, since nothing is lost by replacing it.
	/// </summary>
	private static bool TryReadForChange(string path, out TrayMemory memory)
	{
		memory = new TrayMemory();

		try
		{
			if (!File.Exists(path)) return true;

			var text = File.ReadAllText(path);

			try
			{
				memory = JsonSerializer.Deserialize<TrayMemory>(text, Options) ?? new TrayMemory();
			}
			catch (JsonException)
			{
				// Not a memory; replacing it costs nothing.
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Applies <paramref name="change"/> to the memory as the file holds it now and writes the result, reporting
	/// whether it landed.
	/// </summary>
	public static bool Remember(string path, Func<TrayMemory, TrayMemory> change)
	{
		using var gate = Lock(path);

		return TryReadForChange(path, out var memory) && Write(path, change(memory));
	}

	/// <summary>
	/// Whether to announce <paramref name="version"/>: true only where the file, read now, says no copy has
	/// announced it, in which case it is remembered as announced before this returns, so a second copy asking a
	/// moment later is told no.
	/// </summary>
	/// <param name="path">The memory file.</param>
	/// <param name="version">The newer release the update check found.</param>
	/// <param name="remembered">Whether the announcement was written down; false costs a repeat at the next start.</param>
	public static bool ClaimRelease(string path, string version, out bool remembered)
	{
		using var gate = Lock(path);

		// A file held by something else is not claimed, so not announced; the next check asks again.
		if (!TryReadForChange(path, out var memory))
		{
			remembered = false;
			return false;
		}

		if (!memory.IsNewRelease(available: true, version))
		{
			remembered = true;
			return false;
		}

		remembered = Write(path, memory with { NotifiedRelease = version });
		return true;
	}

	/// <summary>
	/// Writes the whole file. Written beside it and moved over it, so a tray killed mid-write -- which is how a
	/// deploy stops one -- leaves the old memory rather than half of a new one.
	/// </summary>
	private static bool Write(string path, TrayMemory memory)
	{
		var staged = $"{path}.{Environment.ProcessId}.tmp";

		try
		{
			if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

			File.WriteAllText(staged, JsonSerializer.Serialize(memory, Options));
			File.Move(staged, path, overwrite: true);
			return true;
		}
		catch (Exception)
		{
			try
			{
				File.Delete(staged);
			}
			catch (Exception)
			{
				// Nothing to tidy, or nothing that can be; the next write replaces it.
			}

			return false;
		}
	}

	/// <summary>
	/// The lock file beside the memory, held open with no sharing, which every copy of the tray respects; or
	/// null when it cannot be had within <see cref="LockWithin"/>.
	/// </summary>
	private static FileStream? Lock(string path)
	{
		var deadline = DateTime.UtcNow + LockWithin;

		while (true)
		{
			try
			{
				if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

				return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(25);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
