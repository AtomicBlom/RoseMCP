using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Settings;

/// <summary>
/// Reads and changes <see cref="WorkspaceHealthRecord"/> at
/// <c>%LOCALAPPDATA%/BinaryVibrance/RoseMCP/workspace-health.json</c>, beside <c>settings.json</c>.
/// <para>
/// A file of its own rather than more of <see cref="RoseSettings"/>, because the two are written differently.
/// A setting is one person toggling one value in one window; this is written by every broker on the machine --
/// the tray's, and any stdio server's running beside it -- whenever a reason is first seen, and by the person
/// accepting one, so two writers meeting is the ordinary case. Whole-file writes with no lock, which is all a
/// setting needs, would lose whichever change landed first.
/// </para>
/// <para>
/// So a change is a read, a change and a write under a lock held across processes -- a lock file opened for
/// exclusive use -- and the write goes to a temporary file renamed over the real one, so a reader or a crash
/// mid-write never meets half a file. A file that does not parse is moved aside rather than written over,
/// since it is the person's acceptances that would go with it, and one written by a newer build is read but
/// never written over, since this build would drop what it does not understand.
/// </para>
/// <para>
/// Nothing here throws, for the reason <see cref="RoseSettingsFile"/> gives: none of it is load-bearing. A
/// reason that cannot be remembered as accepted is shown as it would have been, which costs a line, not an
/// answer.
/// </para>
/// </summary>
public static class WorkspaceHealthFile
{
	/// <summary>The shape this build writes.</summary>
	public const int CurrentVersion = 1;

	/// <summary>
	/// How long a change waits for another process to finish its own. Changes are a read and a write of a small
	/// file, so anything holding the lock this long has hung, and the change is given up rather than waited on.
	/// </summary>
	private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Within this process, so two threads take turns rather than spinning on each other's lock file.</summary>
	private static readonly Lock Gate = new();

	/// <summary>The file, which need not exist.</summary>
	/// <param name="localAppData">Where local application data is, for a test or a host that wants its own.</param>
	public static string PathFor(string? localAppData = null) =>
		Path.Combine(Path.GetDirectoryName(RoseSettingsFile.PathFor(localAppData))!, "workspace-health.json");

	/// <summary>What is recorded, or nothing recorded where the file is missing or cannot be read.</summary>
	public static WorkspaceHealthRecord Read(string? localAppData = null) =>
		TryRead(PathFor(localAppData), out var record) ? record : new WorkspaceHealthRecord();

	/// <summary>
	/// Applies <paramref name="change"/> to what is on disk now and writes the result, returning it, or null where
	/// it could not be written -- the lock was held too long, the folder cannot be written, or the file is a newer
	/// build's. A change that returns the record it was given writes nothing.
	/// </summary>
	public static WorkspaceHealthRecord? Update(Func<WorkspaceHealthRecord, WorkspaceHealthRecord> change, string? localAppData = null)
	{
		var path = PathFor(localAppData);

		try
		{
			var directory = Path.GetDirectoryName(path)!;
			Directory.CreateDirectory(directory);

			lock (Gate)
			{
				using var held = HoldLock(path + ".lock");
				if (held is null) return null;

				var readable = TryRead(path, out var current);
				if (!readable && File.Exists(path)) File.Move(path, $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);

				if (current.Version > CurrentVersion) return null;

				var changed = change(current);
				if (ReferenceEquals(changed, current)) return current;

				var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

				try
				{
					File.WriteAllText(temporary, JsonSerializer.Serialize(changed with { Version = CurrentVersion }, Options));
					File.Move(temporary, path, overwrite: true);
				}
				finally
				{
					if (File.Exists(temporary)) File.Delete(temporary);
				}

				return changed;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether <paramref name="path"/> held a record that parsed; false with nothing recorded where it is missing,
	/// unreadable or nonsense.
	/// </summary>
	private static bool TryRead(string path, out WorkspaceHealthRecord record)
	{
		record = new WorkspaceHealthRecord();

		try
		{
			if (!File.Exists(path)) return false;

			record = JsonSerializer.Deserialize<WorkspaceHealthRecord>(File.ReadAllText(path), Options) ?? record;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>
	/// The lock file opened for this process alone, or null where another held it for longer than
	/// <see cref="LockWait"/>. The file stays: deleting it on close would let a process that opened the old one and
	/// one that created a new one each believe it held the lock.
	/// </summary>
	private static FileStream? HoldLock(string lockPath)
	{
		var deadline = DateTime.UtcNow + LockWait;

		while (true)
		{
			try
			{
				return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(20);
			}
			catch (IOException)
			{
				return null;
			}
		}
	}
}
