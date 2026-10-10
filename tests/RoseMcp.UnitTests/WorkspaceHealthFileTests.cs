using RoseMcp.Settings;

namespace RoseMcp.UnitTests;

/// <summary>
/// The per-machine record of accepted reasons. Every broker on the machine writes it -- the tray's, and any
/// stdio server's beside it -- so a change is a read, a change and a write under a lock held across
/// processes, and a write is a rename, never a file half-written. What it holds is the person's own choices,
/// so nothing here may lose one: not a race, not a crash, not a file somebody broke by hand.
/// </summary>
public sealed class WorkspaceHealthFileTests
{
	[Test]
	public void The_record_lives_beside_the_settings() =>
		WorkspaceHealthFile.PathFor(@"D:\local").ShouldBe(Path.Combine(@"D:\local", "BinaryVibrance", "RoseMCP", "workspace-health.json"));

	[Test]
	public void Nothing_written_yet_reads_as_nothing_recorded()
	{
		using var home = new TemporaryLocalAppData();

		WorkspaceHealthFile.Read(home.Path).Solutions.ShouldBeEmpty();
	}

	[Test]
	public void What_is_written_is_what_is_read()
	{
		using var home = new TemporaryLocalAppData();
		var accepted = new DateTime(2026, 10, 2, 9, 20, 0, DateTimeKind.Utc);

		var written = WorkspaceHealthFile.Update(record => record with { Solutions = [Solution("A", accepted)] }, home.Path);

		written.ShouldNotBeNull();

		var read = WorkspaceHealthFile.Read(home.Path).Solutions.ShouldHaveSingleItem();
		read.Solution.ShouldBe(Path.Combine(Path.GetTempPath(), "A.slnx"));
		read.Accepted.ShouldHaveSingleItem().AcceptedUtc.ShouldBe(accepted);
		read.Accepted[0].Note.ShouldBe("Expected.");
	}

	/// <summary>
	/// Many changes at once each land: the one that lost the race reads what the winner wrote and changes
	/// that, rather than writing over it from what it read before.
	/// </summary>
	[Test]
	public async Task Changes_made_at_once_all_land()
	{
		using var home = new TemporaryLocalAppData();

		await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
			WorkspaceHealthFile.Update(record => record with { Solutions = [.. record.Solutions, Solution($"S{index}")] }, home.Path)
				.ShouldNotBeNull())));

		WorkspaceHealthFile.Read(home.Path).Solutions.Count.ShouldBe(16);
	}

	/// <summary>
	/// Another process holding the record is waited for, not written over: the lock file is held exclusively for
	/// the read, the change and the write, which is what keeps two brokers from each saving what they read first.
	/// </summary>
	[Test]
	public async Task A_change_waits_for_another_writer_to_finish()
	{
		using var home = new TemporaryLocalAppData();
		var path = WorkspaceHealthFile.PathFor(home.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		WorkspaceHealthRecord? written;

		using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			var waiting = Task.Run(() => WorkspaceHealthFile.Update(record => record with { Solutions = [Solution("A")] }, home.Path));

			await Task.Delay(300);
			waiting.IsCompleted.ShouldBeFalse("a change must wait for the process holding the lock");

			held.Close();
			written = await waiting;
		}

		written.ShouldNotBeNull();
		WorkspaceHealthFile.Read(home.Path).Solutions.ShouldHaveSingleItem();
	}

	/// <summary>A change that changes nothing writes nothing, so seeing the same reasons again costs no write.</summary>
	[Test]
	public void A_change_that_changes_nothing_writes_nothing()
	{
		using var home = new TemporaryLocalAppData();

		WorkspaceHealthFile.Update(record => record, home.Path).ShouldNotBeNull();

		File.Exists(WorkspaceHealthFile.PathFor(home.Path)).ShouldBeFalse();
	}

	/// <summary>
	/// A file somebody broke by hand still holds what they meant, so it is moved aside rather than written over,
	/// and the change lands in a fresh one.
	/// </summary>
	[Test]
	public void A_file_that_does_not_parse_is_moved_aside_rather_than_written_over()
	{
		using var home = new TemporaryLocalAppData();
		var path = WorkspaceHealthFile.PathFor(home.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ \"solutions\": [ broken");

		WorkspaceHealthFile.Read(home.Path).Solutions.ShouldBeEmpty();
		WorkspaceHealthFile.Update(record => record with { Solutions = [Solution("A")] }, home.Path).ShouldNotBeNull();

		WorkspaceHealthFile.Read(home.Path).Solutions.ShouldHaveSingleItem();
		var aside = Directory.GetFiles(Path.GetDirectoryName(path)!, "workspace-health.json.unreadable-*").ShouldHaveSingleItem();
		File.ReadAllText(aside).ShouldBe("{ \"solutions\": [ broken");
	}

	/// <summary>A newer build's record is read, and never written over by a build that would drop what it does not know.</summary>
	[Test]
	public void A_newer_builds_record_is_never_written_over()
	{
		using var home = new TemporaryLocalAppData();
		var path = WorkspaceHealthFile.PathFor(home.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, """{ "version": 99, "solutions": [] }""");

		WorkspaceHealthFile.Update(record => record with { Solutions = [Solution("A")] }, home.Path).ShouldBeNull();

		File.ReadAllText(path).ShouldContain("99", Case.Sensitive);
	}

	/// <summary>Nothing is left beside the record but the record and its lock: a write's temporary file is renamed into place or removed.</summary>
	[Test]
	public void A_write_leaves_no_temporary_file()
	{
		using var home = new TemporaryLocalAppData();

		WorkspaceHealthFile.Update(record => record with { Solutions = [Solution("A")] }, home.Path).ShouldNotBeNull();

		Directory.GetFiles(Path.GetDirectoryName(WorkspaceHealthFile.PathFor(home.Path))!)
			.Select(Path.GetFileName)
			.Order(StringComparer.Ordinal)
			.ShouldBe(["workspace-health.json", "workspace-health.json.lock"]);
	}

	private static SolutionHealthRecord Solution(string name, DateTime? accepted = null) => new()
	{
		Solution = Path.Combine(Path.GetTempPath(), $"{name}.slnx"),
		Accepted =
		[
			new AcceptedReasonRecord
			{
				Kind = "project-not-loaded",
				Project = "Legacy.Tests",
				AcceptedUtc = accepted ?? DateTime.UtcNow,
				AgentsRead = "Note",
				Note = "Expected.",
			},
		],
	};
}
