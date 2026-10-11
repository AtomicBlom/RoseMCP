using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Settings;

namespace RoseMcp.UnitTests;

/// <summary>
/// The rules every start keeps, an agent's and a person's alike, because both go through the session manager's own
/// attach and launch: refused before any host is started, each saying why.
/// </summary>
public sealed class DebugStartRulesTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "rose-debug-start-rules", Guid.NewGuid().ToString("N"));

	public DebugStartRulesTests() => Directory.CreateDirectory(_directory);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// The temp sweep has it.
		}
	}

	private static LiveAppSessionManager Manager() =>
		new(Options.Create(new BrokerOptions()), NullLoggerFactory.Instance, NullLogger<LiveAppSessionManager>.Instance);

	/// <summary>A system process is refused, for a person as for an agent.</summary>
	[Test]
	public async Task A_system_process_is_refused_for_everyone()
	{
		await using var sessions = Manager();

		foreach (var owners in new[] { LiveAppOwners.Person, LiveAppOwners.Agent("mcp-one") })
		{
			var refused = await Should.ThrowAsync<ArgumentException>(() => sessions.AttachAsync(4, owners, CancellationToken.None));
			refused.Message.ShouldContain("system process");
		}

		sessions.Sessions.ShouldBeEmpty();
	}

	/// <summary>
	/// The broker's own process is refused: a debugger stopping it at a breakpoint stops what serves the debugger,
	/// and nothing could then let it go.
	/// </summary>
	[Test]
	public async Task The_brokers_own_process_is_refused()
	{
		await using var sessions = Manager();

		var refused = await Should.ThrowAsync<ArgumentException>(
			() => sessions.AttachAsync(Environment.ProcessId, LiveAppOwners.Person, CancellationToken.None));

		refused.Message.ShouldContain("this broker");
	}

	/// <summary>A process that is not running is said to be not running, rather than handed to a host that would fault.</summary>
	[Test]
	public async Task A_process_that_is_not_running_is_refused()
	{
		await using var sessions = Manager();

		var refused = await Should.ThrowAsync<ArgumentException>(
			() => sessions.AttachAsync(int.MaxValue - 7, LiveAppOwners.Person, CancellationToken.None));

		refused.Message.ShouldContain("No local process");
	}

	/// <summary>
	/// A program is named in full, exists, and is an executable: a window has no folder to measure a relative path
	/// from, and a missing or non-executable file would only fault a host.
	/// </summary>
	[Test]
	public async Task A_launch_names_an_executable_that_exists_in_full()
	{
		await using var sessions = Manager();
		var text = Path.Combine(_directory, "notes.txt");
		await File.WriteAllTextAsync(text, "not a program");

		(await Should.ThrowAsync<ArgumentException>(() => sessions.LaunchAsync("bin/App.exe", null, LiveAppOwners.Person, CancellationToken.None)))
			.Message.ShouldContain("is not a full path");

		(await Should.ThrowAsync<ArgumentException>(() => sessions.LaunchAsync(Path.Combine(_directory, "Missing.exe"), null, LiveAppOwners.Person, CancellationToken.None)))
			.Message.ShouldContain("No executable at");

		(await Should.ThrowAsync<ArgumentException>(() => sessions.LaunchAsync(text, null, LiveAppOwners.Person, CancellationToken.None)))
			.Message.ShouldContain("is not an executable");

		(await Should.ThrowAsync<ArgumentException>(() => sessions.LaunchAsync("  ", null, LiveAppOwners.Person, CancellationToken.None)))
			.Message.ShouldContain("names the program to run");

		sessions.Sessions.ShouldBeEmpty();
	}

	/// <summary>A packaged app is named as PackageFamilyName!AppId, with nothing around it.</summary>
	[Test]
	[Arguments("Drawboard.PDF_gqbn7fs4pywxm")]
	[Arguments("!App")]
	[Arguments("Drawboard.PDF_gqbn7fs4pywxm!")]
	[Arguments("Drawboard.PDF_gqbn7fs4pywxm!App!Two")]
	[Arguments("Drawboard PDF!App")]
	public async Task A_packaged_app_is_named_by_its_id(string aumid)
	{
		await using var sessions = Manager();

		(await Should.ThrowAsync<ArgumentException>(() => sessions.LaunchPackageAsync(aumid, null, LiveAppOwners.Person, CancellationToken.None)))
			.Message.ShouldContain("is not an application user-model id");
	}

	/// <summary>An id with space around it is taken trimmed, which is how one pasted from a terminal arrives.</summary>
	[Test]
	public void A_packaged_apps_id_is_trimmed() =>
		LiveAppSessionManager.PackagedApp("  Drawboard.PDF_gqbn7fs4pywxm!App \n").ShouldBe("Drawboard.PDF_gqbn7fs4pywxm!App");

	/// <summary>Sharing, unsharing and closing a session that is not open are answers rather than failures.</summary>
	[Test]
	public async Task Sharing_a_session_that_is_not_open_finds_nothing()
	{
		await using var sessions = Manager();
		var signalled = sessions.Changed.Next;

		(await sessions.ShareAsync("session-none", new LiveAppSharedClient("relay:relayed-one", "client-1", null), CancellationToken.None)).ShouldBeNull();
		(await sessions.UnshareAsync("session-none", CancellationToken.None)).ShouldBeNull();
		(await sessions.CloseAsync("session-none", CancellationToken.None)).ShouldBeFalse();
		signalled.IsCompleted.ShouldBeFalse("nothing changed, so nothing is woken");
	}

	/// <summary>A signal made of others completes at the next pulse of any of them.</summary>
	[Test]
	public async Task Either_signal_wakes_a_wait_on_both()
	{
		var calls = new ChangeSignal();
		var starts = new ChangeSignal();
		var either = ChangeSignal.Either(calls, starts);

		var first = either.Next;
		starts.Pulse();
		await first.WaitAsync(TimeSpan.FromSeconds(5));

		var second = either.Next;
		second.IsCompleted.ShouldBeFalse();
		calls.Pulse();
		await second.WaitAsync(TimeSpan.FromSeconds(5));
	}

	/// <summary>
	/// What a person debugged lately is kept newest first, each target once -- a program with the same arguments is
	/// the same target -- and at most ten of them.
	/// </summary>
	[Test]
	public void Recent_targets_are_newest_first_each_once_and_bounded()
	{
		var start = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

		RecentTargetRecord Program(string name, string? arguments, int minutes) => new()
		{
			Kind = "LaunchExecutable",
			DisplayName = name,
			ExecutablePath = $@"D:\Tools\{name}",
			Arguments = arguments,
			LastUsedUtc = start.AddMinutes(minutes),
		};

		RecentTargetsFile.Read(_directory).ShouldBeEmpty();

		RecentTargetsFile.Record(Program("InkLab.exe", "--sample strokes.json", 0), _directory).ShouldBeTrue();
		RecentTargetsFile.Record(Program("Other.exe", null, 1), _directory).ShouldBeTrue();
		RecentTargetsFile.Record(Program("InkLab.exe", "--sample strokes.json", 2), _directory).ShouldBeTrue();
		RecentTargetsFile.Record(Program("InkLab.exe", "--other", 3), _directory).ShouldBeTrue();

		RecentTargetsFile.Read(_directory)
			.Select(target => $"{target.DisplayName} {target.Arguments}".Trim())
			.ShouldBe(["InkLab.exe --other", "InkLab.exe --sample strokes.json", "Other.exe"]);

		for (var index = 0; index < 20; index++) RecentTargetsFile.Record(Program($"App{index}.exe", null, 10 + index), _directory);

		var kept = RecentTargetsFile.Read(_directory);
		kept.Count.ShouldBe(RecentTargetsFile.Capacity);
		kept[0].DisplayName.ShouldBe("App19.exe");
	}

	/// <summary>A file that is not a list reads as an empty one rather than failing whoever asked.</summary>
	[Test]
	public void An_unreadable_recent_file_is_an_empty_list()
	{
		var path = RecentTargetsFile.PathFor(_directory);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ not a list");

		RecentTargetsFile.Read(_directory).ShouldBeEmpty();
	}
}
