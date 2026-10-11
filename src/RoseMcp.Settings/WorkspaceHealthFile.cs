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
	/// <summary>
	/// The shape this build writes. Version 2 names what an acceptance covers <c>covering</c>, a project or a
	/// subject, where version 1 said <c>project</c>; it is read either way, and a build that knows only version 1
	/// reads a version 2 file and refuses to write over it rather than drop the field it does not know.
	/// </summary>
	public const int CurrentVersion = 2;

	/// <summary>
	/// How long a change waits, by default, for another process to finish with the record: its lock, or a read
	/// standing in the way of the replace. Changes are a read and a write of a small file, so anything holding it
	/// this long has hung, and the change is given up rather than waited on.
	/// </summary>
	public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

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
		ReadAt(PathFor(localAppData), out record, out _) != ReadOutcome.Unreadable;

	/// <summary>
	/// Applies <paramref name="change"/> to what is on disk now and writes the result. The answer carries the
	/// record written, or what stopped it -- the lock held too long, the file unreadable or not replaceable, the
	/// folder not writable, a newer build's file -- in words, since the person accepting a reason is owed the
	/// reason it did not stick. Nothing is written over what could not be read: only a file that was read and did
	/// not parse is moved aside, since that is the one whose acceptances are already lost to this build. A change
	/// that returns the record it was given writes nothing.
	/// </summary>
	/// <param name="change">What to do to the record on disk.</param>
	/// <param name="localAppData">Where local application data is, for a test or a host that wants its own.</param>
	/// <param name="wait">How long to wait for another process to let go of the record; <see cref="DefaultWait"/> by default.</param>
	public static WorkspaceHealthWrite Update(
		Func<WorkspaceHealthRecord, WorkspaceHealthRecord> change,
		string? localAppData = null,
		TimeSpan? wait = null)
	{
		var path = PathFor(localAppData);
		var patience = wait ?? DefaultWait;

		try
		{
			var directory = Path.GetDirectoryName(path)!;
			Directory.CreateDirectory(directory);

			lock (Gate)
			{
				using var held = HoldLock(path + ".lock", patience);
				if (held is null)
				{
					return WorkspaceHealthWrite.Failed($"Another process held {path}.lock for longer than {patience.TotalSeconds:0.#} s.");
				}

				var outcome = ReadAt(path, out var current, out var unreadable);
				if (outcome == ReadOutcome.Unreadable) return WorkspaceHealthWrite.Failed($"{path} could not be read: {unreadable}");

				if (outcome == ReadOutcome.Unparsable)
				{
					File.Move(path, $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
				}

				if (current.Version > CurrentVersion)
				{
					return WorkspaceHealthWrite.Failed(
						$"{path} was written by a newer RoseMCP, in version {current.Version} of its shape, and this one writes "
							+ $"version {CurrentVersion}, so it leaves the file as it is.");
				}

				var changed = change(current);
				if (ReferenceEquals(changed, current)) return WorkspaceHealthWrite.Wrote(current);

				var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

				try
				{
					File.WriteAllText(temporary, JsonSerializer.Serialize(changed with { Version = CurrentVersion }, Options));

					if (ReplaceWith(temporary, path, patience) is { } refused)
					{
						return WorkspaceHealthWrite.Failed(
							$"{path} could not be replaced within {patience.TotalSeconds:0.#} s, since another process holds it open: {refused}");
					}
				}
				finally
				{
					if (File.Exists(temporary)) File.Delete(temporary);
				}

				return WorkspaceHealthWrite.Wrote(changed);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return WorkspaceHealthWrite.Failed($"{path} could not be written: {exception.Message}");
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
	/// <paramref name="record"/> in this version's shape: what version 1 named <c>project</c> is
	/// <c>covering</c> here, so an acceptance made by an earlier build keeps covering what it covered.
	/// </summary>
	private static WorkspaceHealthRecord Upgraded(WorkspaceHealthRecord record) => record with
	{
		Solutions = [.. record.Solutions.Select(solution => solution with
		{
			Accepted = [.. solution.Accepted.Select(accepted => accepted.Project is { Length: > 0 } project && accepted.Covering.Length == 0
				? accepted with { Covering = project, Project = null }
				: accepted with { Project = null })],
			Seen = [.. solution.Seen.Select(seen => seen.Project is { Length: > 0 } project && seen.Covering.Length == 0
				? seen with { Covering = project, Project = null }
				: seen with { Project = null })],
		})],
	};

	/// <summary>
	/// Reads <paramref name="path"/>, nothing recorded unless it was read and parsed, with why it could not be read
	/// where it could not. Opened sharing everything, deletion included, so a reader stands in a writer's way for
	/// no longer than the read takes, which the writer waits out (<see cref="ReplaceWith"/>).
	/// </summary>
	private static ReadOutcome ReadAt(string path, out WorkspaceHealthRecord record, out string? unreadable)
	{
		record = new WorkspaceHealthRecord();
		unreadable = null;

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
			unreadable = exception.Message;
			return ReadOutcome.Unreadable;
		}

		try
		{
			record = Upgraded(JsonSerializer.Deserialize<WorkspaceHealthRecord>(text, Options) ?? record);
			return ReadOutcome.Read;
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			return ReadOutcome.Unparsable;
		}
	}

	/// <summary>
	/// Renames <paramref name="temporary"/> over <paramref name="path"/>, waiting out a reader that has it open,
	/// and answers null, or what the last refusal said where it was still refused when <paramref name="wait"/> ran
	/// out. Windows refuses to replace a file any process has open, however that process shares it, and every
	/// reader here holds it only for the moment a read takes -- so a refusal is retried rather than lose the change
	/// to a reader that was about to let go.
	/// </summary>
	private static string? ReplaceWith(string temporary, string path, TimeSpan wait)
	{
		var deadline = DateTime.UtcNow + wait;

		while (true)
		{
			try
			{
				File.Move(temporary, path, overwrite: true);
				return null;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (DateTime.UtcNow >= deadline) return exception.Message;

				Thread.Sleep(20);
			}
		}
	}

	/// <summary>
	/// The lock file opened for this process alone, or null where another held it for longer than
	/// <paramref name="wait"/>. The file stays: deleting it on close would let a process that opened the old one and
	/// one that created a new one each believe it held the lock.
	/// </summary>
	private static FileStream? HoldLock(string lockPath, TimeSpan wait)
	{
		var deadline = DateTime.UtcNow + wait;

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
