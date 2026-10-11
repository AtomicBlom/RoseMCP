using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Settings;

/// <summary>
/// Reads and records what a person debugged from a window lately, at
/// <c>%LOCALAPPDATA%/BinaryVibrance/RoseMCP/recent-debug-targets.json</c>, beside <c>settings.json</c>: the newest
/// <see cref="Capacity"/>, each once.
/// <para>
/// A file of its own rather than part of <see cref="RoseSettings"/>, because it is written by a broker as a person
/// debugs rather than by a person toggling a preference. Written whole, to a temporary file renamed over the real one,
/// under a lock within the process: only a broker serving the operator API writes it, and two of those on one machine
/// at the same moment lose one entry at worst, which a list of recent targets can afford where a person's acceptances
/// cannot.
/// </para>
/// <para>
/// Nothing here throws, for the reason <see cref="RoseSettingsFile"/> gives: a list that cannot be read is an empty
/// one, and a target that could not be recorded is debugged all the same.
/// </para>
/// </summary>
public static class RecentTargetsFile
{
	/// <summary>The most targets kept.</summary>
	public const int Capacity = 10;

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private static readonly Lock Gate = new();

	/// <summary>The file, which need not exist.</summary>
	/// <param name="localAppData">Where local application data is, for a test or a host that wants its own.</param>
	public static string PathFor(string? localAppData = null) =>
		Path.Combine(Path.GetDirectoryName(RoseSettingsFile.PathFor(localAppData))!, "recent-debug-targets.json");

	/// <summary>The targets recorded, newest first; none where the file is missing or cannot be read.</summary>
	public static IReadOnlyList<RecentTargetRecord> Read(string? localAppData = null)
	{
		try
		{
			var path = PathFor(localAppData);
			if (!File.Exists(path)) return [];

			var targets = JsonSerializer.Deserialize<List<RecentTargetRecord>>(File.ReadAllText(path), Options) ?? [];

			return [.. targets.Where(target => target.DisplayName.Length > 0).OrderByDescending(target => target.LastUsedUtc).Take(Capacity)];
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary>
	/// Records <paramref name="target"/> as the newest, in place of any earlier entry for the same target, keeping the
	/// newest <see cref="Capacity"/>. False where the file could not be written.
	/// </summary>
	public static bool Record(RecentTargetRecord target, string? localAppData = null)
	{
		ArgumentNullException.ThrowIfNull(target);

		try
		{
			var path = PathFor(localAppData);
			var directory = Path.GetDirectoryName(path)!;

			lock (Gate)
			{
				var kept = Read(localAppData).Where(existing => !existing.SameTarget(target));
				var written = new[] { target }.Concat(kept).Take(Capacity).ToList();

				Directory.CreateDirectory(directory);
				var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

				try
				{
					File.WriteAllText(temporary, JsonSerializer.Serialize(written, Options));
					File.Move(temporary, path, overwrite: true);
				}
				finally
				{
					if (File.Exists(temporary)) File.Delete(temporary);
				}
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return false;
		}
	}
}

/// <summary>Something a person debugged from a window, as <see cref="RecentTargetsFile"/> keeps it.</summary>
public sealed record RecentTargetRecord
{
	/// <summary><c>AttachProcess</c>, <c>LaunchExecutable</c> or <c>LaunchUwp</c>.</summary>
	public required string Kind { get; init; }

	/// <summary>What a window calls it.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The program launched, or the executable of the process attached to.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>The arguments a program was launched with.</summary>
	public string? Arguments { get; init; }

	/// <summary>The packaged app launched.</summary>
	public string? AppUserModelId { get; init; }

	/// <summary>When it was last debugged.</summary>
	public required DateTime LastUsedUtc { get; init; }

	/// <summary>
	/// Whether the two name the same target: the same kind, the same program and arguments or the same packaged app.
	/// Paths and ids are compared ignoring case, as Windows compares them.
	/// </summary>
	public bool SameTarget(RecentTargetRecord other)
	{
		ArgumentNullException.ThrowIfNull(other);

		return string.Equals(Kind, other.Kind, StringComparison.Ordinal)
			&& string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(Arguments ?? string.Empty, other.Arguments ?? string.Empty, StringComparison.Ordinal)
			&& string.Equals(AppUserModelId, other.AppUserModelId, StringComparison.OrdinalIgnoreCase);
	}
}
