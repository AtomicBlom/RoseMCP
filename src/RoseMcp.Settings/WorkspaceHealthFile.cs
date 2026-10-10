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
/// never written over, since this build would drop what it does not understand. One that cannot be read at
/// all -- another process holds it this moment -- is left alone and the change fails, since that says nothing
/// about what is in it.
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
		TryRead(localAppData, out var record) ? record : new WorkspaceHealthRecord();

	/// <summary>
	/// What is recorded, nothing recorded where the file is missing or does not parse, and false where it could not
	/// be read at all -- held by another process, or not readable by this user -- so a reader keeping what it read
	/// last can keep it rather than replace every acceptance with none for as long as a moment's sharing lasts.
	/// </summary>
	public static bool TryRead(string? localAppData, out WorkspaceHealthRecord record) =>
		ReadAt(PathFor(localAppData), out record) != ReadOutcome.Unreadable;

	/// <summary>
	/// Applies <paramref name="change"/> to what is on disk now and writes the result, returning it, or null where
	/// it could not be written -- the lock was held too long, the file or the folder could not be read or written,
	/// or the file is a newer build's. Nothing is written over what could not be read: only a file that was read and
	/// did not parse is moved aside, since that is the one whose acceptances are already lost to this build. A
	/// change that returns the record it was given writes nothing.
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

				var outcome = ReadAt(path, out var current);
				if (outcome == ReadOutcome.Unreadable) return null;

				if (outcome == ReadOutcome.Unparsable)
				{
					File.Move(path, $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
				}

				if (current.Version > CurrentVersion) return null;

				var changed = change(current);
				if (ReferenceEquals(changed, current)) return current;

				var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

				try
				{
					File.WriteAllText(temporary, JsonSerializer.Serialize(changed with { Version = CurrentVersion }, Options));
					ReplaceWith(temporary, path);
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

	/// <summary>How reading the file went.</summary>
	private enum ReadOutcome
	{
		/// <summary>There is no file, and nothing is recorded.</summary>
		Missing,

		/// <summary>The file was read and parsed.</summary>
		Read,

		/// <summary>The file was read and is not a record: hand-edited into nonsense, or cut short.</summary>
		Unparsable,

		/// <summary>The file could not be read at all, which says nothing about what is in it.</summary>
		Unreadable,
	}

	/// <summary>
	/// Reads <paramref name="path"/>, nothing recorded unless it was read and parsed. Opened sharing everything,
	/// deletion included, so a reader stands in a writer's way for no longer than the read takes, which the writer
	/// waits out (<see cref="ReplaceWith"/>).
	/// </summary>
	private static ReadOutcome ReadAt(string path, out WorkspaceHealthRecord record)
	{
		record = new WorkspaceHealthRecord();

		string text;
		try
		{
			if (!File.Exists(path)) return ReadOutcome.Missing;

			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			text = reader.ReadToEnd();
		}
		catch (FileNotFoundException)
		{
			return ReadOutcome.Missing;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ReadOutcome.Unreadable;
		}

		try
		{
			record = JsonSerializer.Deserialize<WorkspaceHealthRecord>(text, Options) ?? record;
			return ReadOutcome.Read;
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			return ReadOutcome.Unparsable;
		}
	}

	/// <summary>
	/// Renames <paramref name="temporary"/> over <paramref name="path"/>, waiting out a reader that has it open.
	/// Windows refuses to replace a file any process has open, however that process shares it, and every reader
	/// here holds it only for the moment a read takes -- so a refusal is retried until <see cref="LockWait"/>
	/// rather than lose the change to a reader that was about to let go.
	/// </summary>
	private static void ReplaceWith(string temporary, string path)
	{
		var deadline = DateTime.UtcNow + LockWait;

		while (true)
		{
			try
			{
				File.Move(temporary, path, overwrite: true);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
			{
				Thread.Sleep(20);
			}
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
