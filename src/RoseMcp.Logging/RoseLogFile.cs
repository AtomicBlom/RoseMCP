using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RoseMcp.Logging;

/// <summary>
/// Where a process's log goes and what it is called.
/// <para>
/// Separate from the sink wiring because the naming is the part with rules worth testing: a
/// worker's file has to name the solution it owns, two processes must never claim the same file,
/// and old sessions have to be reclaimed by something -- Serilog's own retention cannot do it,
/// since it only prunes files sharing one rolling base name and every session here has its own.
/// </para>
/// </summary>
public static partial class RoseLogFile
{
	/// <summary>Session files kept per component before the oldest are deleted.</summary>
	public const int DefaultSessionsKept = 20;

	/// <summary>Size at which a single session rolls to a second part.</summary>
	public const long DefaultFileSizeLimitBytes = 100L * 1024 * 1024;

	/// <summary>Parts one session may leave behind, so a runaway cannot fill the disk.</summary>
	public const int DefaultPartsPerSession = 3;

	/// <summary>
	/// %LOCALAPPDATA%/BinaryVibrance/RoseMCP/{component}. The root is a parameter so a test can
	/// point it somewhere disposable rather than at the machine's real profile.
	/// </summary>
	public static string DirectoryFor(string component, string? localAppData = null)
	{
		var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return Path.Combine(root, "BinaryVibrance", "RoseMCP", component);
	}

	/// <summary>
	/// A solution path reduced to one readable filename component.
	/// <para>
	/// The name alone would collide across checkouts -- two worktrees of the same repository are
	/// the normal case here, not a corner one -- so a hash of the full path is appended. It is
	/// lowercased first because Windows paths are case-insensitive and the same solution reached
	/// through differently-cased paths is the same solution.
	/// </para>
	/// </summary>
	public static string EncodeSolutionPath(string solutionPath)
	{
		var full = Path.GetFullPath(solutionPath);
		var hash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())).AsSpan(0, 4)).ToLowerInvariant();

		// Lowercased along with the hash. A log file name is not prose, and one solution reached
		// through two differently-cased paths producing two differently-named files is worse than
		// losing the capitals.
		var readable = InvalidNameCharacters().Replace(Path.GetFileName(full), "_").ToLowerInvariant();
		if (readable.Length == 0) readable = "solution";

		return $"{readable}-{hash}";
	}

	/// <summary>
	/// The file this session writes to, guaranteed not to be one another session is already using.
	/// <para>
	/// The timestamp is UTC to the second, which two workers for one solution can share if the
	/// broker starts them together, so a claimed name is probed for and suffixed rather than
	/// assumed. Serilog takes an exclusive lock, and a losing process would otherwise log nothing.
	/// </para>
	/// </summary>
	public static string Claim(string directory, string? solutionPath, DateTimeOffset utcNow)
	{
		Directory.CreateDirectory(directory);

		var prefix = solutionPath is null ? string.Empty : EncodeSolutionPath(solutionPath) + "-";
		var stamp = utcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

		for (var attempt = 1; ; attempt++)
		{
			var suffix = attempt == 1 ? string.Empty : $"-{attempt}";
			var candidate = Path.Combine(directory, $"{prefix}{stamp}{suffix}.log");

			// Create it here rather than leaving it to the sink: the existence check and the claim
			// have to be one step, or two processes racing both see nothing and both pick it.
			try
			{
				using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
				{
				}

				return candidate;
			}
			catch (IOException) when (attempt < 100)
			{
			}
		}
	}

	/// <summary>
	/// Deletes all but the newest <paramref name="keep"/> sessions in a component's directory.
	/// <para>
	/// Grouped by session rather than by file, because one session that rolled past the size limit
	/// owns several files and they age out together. A file a live process still holds open cannot
	/// be deleted; that is expected -- other workers are running -- and is skipped rather than
	/// treated as a failure. Logging must never be the reason a process does not start.
	/// </para>
	/// </summary>
	public static void PruneSessions(string directory, int keep = DefaultSessionsKept)
	{
		if (!Directory.Exists(directory)) return;

		try
		{
			var sessions = Directory.EnumerateFiles(directory, "*.log")
				.Select(path => new FileInfo(path))
				.GroupBy(file => SessionKeyOf(file.Name), StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(session => session.Max(file => file.LastWriteTimeUtc))
				.Skip(keep);

			foreach (var file in sessions.SelectMany(session => session))
			{
				try
				{
					file.Delete();
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>
	/// The session a file belongs to, with any Serilog roll suffix removed, so that
	/// <c>A-20260901-120000_001.log</c> and <c>A-20260901-120000.log</c> group together.
	/// </summary>
	private static string SessionKeyOf(string fileName) =>
		RollSuffix().Replace(Path.GetFileNameWithoutExtension(fileName), string.Empty);

	[GeneratedRegex(@"_\d{3}$")]
	private static partial Regex RollSuffix();

	[GeneratedRegex(@"[^A-Za-z0-9._-]")]
	private static partial Regex InvalidNameCharacters();
}
