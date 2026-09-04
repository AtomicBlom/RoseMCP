using Microsoft.Extensions.Logging;

using RoseMcp.Logging;

namespace RoseMcp.UnitTests;

/// <summary>
/// The rules the log file naming has to keep: one file per session, never shared between two
/// processes, traceable back to the solution it describes, and reclaimed before it fills a disk.
/// </summary>
public sealed class LoggingTests : IDisposable
{
	private readonly string _root =
		Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"log-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	/// <summary>
	/// A rooted path for whichever platform this is running on.
	/// <para>
	/// Literal <c>C:\...</c> is what these tests used to pass, and it is not a path off Windows: with
	/// no leading separator <see cref="Path.GetFullPath(string)"/> reads it as relative and prepends
	/// the working directory, and <see cref="Path.GetFileName(string)"/> then returns the whole thing
	/// because a backslash is an ordinary character. So the assertions failed for a reason that had
	/// nothing to do with what they were testing.
	/// </para>
	/// </summary>
	private static string Rooted(params string[] segments) =>
		Path.Combine([OperatingSystem.IsWindows() ? @"C:\" : "/", .. segments]);

	[Fact]
	public void Puts_a_component_under_the_vendor_and_product_folders()
	{
		var directory = RoseLogFile.DirectoryFor("Worker", _root);

		Assert.Equal(Path.Combine(_root, "BinaryVibrance", "RoseMCP", "Worker"), directory);
	}

	[Fact]
	public void Keeps_the_solution_name_readable_in_the_encoded_form()
	{
		var encoded = RoseLogFile.EncodeSolutionPath(Rooted("Dev", "Personal", "RoseMCP", "RoseMcp.slnx"));

		Assert.StartsWith("rosemcp.slnx-", encoded, StringComparison.Ordinal);
		Assert.DoesNotContain(Path.DirectorySeparatorChar, encoded);
		Assert.DoesNotContain(':', encoded);
	}

	/// <summary>
	/// Two worktrees of one repository are the normal case, not a corner one, and their solutions
	/// share a file name. The encoded form has to tell them apart or their logs interleave.
	/// </summary>
	[Fact]
	public void Separates_two_checkouts_that_share_a_solution_name()
	{
		var first = RoseLogFile.EncodeSolutionPath(Rooted("repo", "main", "A.slnx"));
		var second = RoseLogFile.EncodeSolutionPath(Rooted("repo", "feature", "A.slnx"));

		Assert.NotEqual(first, second);
	}

	/// <summary>
	/// Case is folded exactly where the filesystem folds it, which is why this asserts two different
	/// things on two platforms rather than one thing everywhere.
	/// <para>
	/// On Windows the two paths are one file, so they have to encode the same or one solution writes
	/// two log files. On Linux they are two files, and giving them one name is worse than it sounds:
	/// Serilog takes an exclusive lock, so the loser of a shared name logs nothing at all. That is
	/// the failure <c>Claim</c> exists to prevent, and folding case here would reintroduce it a level
	/// above.
	/// </para>
	/// </summary>
	[Fact]
	public void Folds_case_exactly_where_the_filesystem_does()
	{
		var upper = RoseLogFile.EncodeSolutionPath(Rooted("Repo", "A.slnx"));
		var lower = RoseLogFile.EncodeSolutionPath(Rooted("repo", "a.slnx"));

		if (OperatingSystem.IsWindows())
		{
			Assert.Equal(upper, lower);
			return;
		}

		Assert.NotEqual(upper, lower);
	}

	[Fact]
	public void Names_a_workers_file_after_its_solution_and_a_hosts_file_after_nothing()
	{
		var directory = RoseLogFile.DirectoryFor("Worker", _root);
		var now = new DateTimeOffset(2026, 9, 1, 14, 30, 22, TimeSpan.Zero);

		var worker = RoseLogFile.Claim(directory, Rooted("repo", "A.slnx"), now);
		var host = RoseLogFile.Claim(RoseLogFile.DirectoryFor("Tray", _root), null, now);

		Assert.EndsWith("-20260901-143022.log", worker, StringComparison.Ordinal);
		Assert.Contains("a.slnx-", Path.GetFileName(worker), StringComparison.Ordinal);
		Assert.Equal("20260901-143022.log", Path.GetFileName(host));
	}

	/// <summary>
	/// The broker can start two workers inside one second. Serilog takes an exclusive lock, so the
	/// loser of a shared name logs nothing at all -- which is why the name is claimed, not assumed.
	/// </summary>
	[Fact]
	public void Claims_a_different_file_when_the_second_is_taken()
	{
		var directory = RoseLogFile.DirectoryFor("Worker", _root);
		var now = new DateTimeOffset(2026, 9, 1, 14, 30, 22, TimeSpan.Zero);

		var first = RoseLogFile.Claim(directory, null, now);
		var second = RoseLogFile.Claim(directory, null, now);

		Assert.NotEqual(first, second);
		Assert.Equal("20260901-143022.log", Path.GetFileName(first));
		Assert.Equal("20260901-143022-2.log", Path.GetFileName(second));
	}

	[Fact]
	public void Prunes_the_oldest_sessions_and_keeps_the_newest()
	{
		var directory = RoseLogFile.DirectoryFor("Worker", _root);
		Directory.CreateDirectory(directory);

		for (var session = 0; session < 6; session++)
		{
			var path = Path.Combine(directory, $"20260901-1200{session:D2}.log");
			File.WriteAllText(path, "x");
			File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 1, 12, 0, session, DateTimeKind.Utc));
		}

		RoseLogFile.PruneSessions(directory, keep: 2);

		var left = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray();
		Assert.Equal(["20260901-120004.log", "20260901-120005.log"], left);
	}

	/// <summary>
	/// A session that outgrew the size limit owns several files. They are one session and age out
	/// together, or pruning keeps a tail with no beginning.
	/// </summary>
	[Fact]
	public void Keeps_the_parts_of_one_rolled_session_together()
	{
		var directory = RoseLogFile.DirectoryFor("Worker", _root);
		Directory.CreateDirectory(directory);

		foreach (var name in (string[])["20260901-120000.log", "20260901-120000_001.log", "20260901-120000_002.log"])
		{
			var path = Path.Combine(directory, name);
			File.WriteAllText(path, "x");
			File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
		}

		var older = Path.Combine(directory, "20260901-110000.log");
		File.WriteAllText(older, "x");
		File.SetLastWriteTimeUtc(older, new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc));

		RoseLogFile.PruneSessions(directory, keep: 1);

		Assert.Equal(3, Directory.GetFiles(directory).Length);
		Assert.False(File.Exists(older));
	}

	[Fact]
	public void Writes_what_was_logged_to_the_file()
	{
		using (var factory = LoggerFactory.Create(logging =>
			logging.SetMinimumLevel(LogLevel.Information).AddRoseFileLogging("Worker", null, _root)))
		{
			factory.CreateLogger("Test").LogInformation("a distinctive line");
		}

		var directory = RoseLogFile.DirectoryFor("Worker", _root);
		var written = Directory.GetFiles(directory, "*.log").Select(File.ReadAllText);

		Assert.Contains(written, text => text.Contains("a distinctive line", StringComparison.Ordinal));
	}

	/// <summary>
	/// In stdio mode stdout carries protocol frames and nothing else, so a sink that writes there
	/// corrupts the stream and the failure reads as an unintelligible protocol error. A console
	/// sink added to this pipeline by mistake is exactly how that would happen.
	/// </summary>
	[Fact]
	public void Writes_nothing_at_all_to_stdout()
	{
		var original = Console.Out;
		var stdout = new StringWriter();

		try
		{
			Console.SetOut(stdout);

			using var factory = LoggerFactory.Create(logging =>
				logging.SetMinimumLevel(LogLevel.Trace).AddRoseFileLogging("Worker", null, _root));

			factory.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "a distinctive line");
		}
		finally
		{
			Console.SetOut(original);
		}

		Assert.Equal(string.Empty, stdout.ToString());
	}
}
