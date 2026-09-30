using System.ComponentModel;
using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which provider sandbox folders a starting live-app host deletes. Every folder holds a copy of the
/// provider and a grant to ALL APPLICATION PACKAGES, and the two ways this goes wrong are both silent:
/// a folder kept for a host that is gone accumulates in the user's temp directory, and a folder deleted
/// under a host that is running pulls the provider out from under it.
/// </summary>
public sealed class SandboxSweepTests
{
	private const int OwnProcessId = 1;

	/// <summary>When every folder in these tests was made, unless a test says otherwise.</summary>
	private static readonly DateTime Made = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

	/// <summary>A folder named for a pid, composed on the running platform so its name parses there.</summary>
	private static string Folder(int pid) => Path.Combine("RoseMcpXaml", pid.ToString(CultureInfo.InvariantCulture));

	private static SandboxSweepPlan Plan(
		IEnumerable<string> folders, Func<int, SandboxProcess> process, Func<string, DateTime>? created = null) =>
		SandboxSweep.Plan(folders, OwnProcessId, created ?? (_ => Made), process);

	[Test]
	public void A_folder_whose_host_is_gone_is_stale()
	{
		var plan = Plan([Folder(100)], _ => SandboxProcess.None);

		plan.Stale.ShouldBe([Folder(100)]);
	}

	/// <summary>Started before the folder was made, so it is the host that made it.</summary>
	[Test]
	public void A_folder_whose_host_is_running_is_kept()
	{
		var plan = Plan([Folder(100)], _ => SandboxProcess.StartedAt(Made.AddSeconds(-2)));

		plan.Stale.ShouldBeEmpty();
		plan.Running.ShouldBe(1);
	}

	/// <summary>
	/// Started after the folder was made, so the id has been reused and the host is gone. Without this a
	/// folder whose id a long-running process picked up stays for as long as that process does.
	/// </summary>
	[Test]
	public void A_folder_whose_id_was_reused_is_stale()
	{
		var plan = Plan([Folder(100)], _ => SandboxProcess.StartedAt(Made.AddMinutes(5)));

		plan.Stale.ShouldBe([Folder(100)]);
	}

	/// <summary>
	/// A process that refuses even a limited query is not this user's, and a host that made a folder in
	/// this user's temp directory runs as this user. A system service holding a reused id is the case.
	/// </summary>
	[Test]
	public void A_folder_whose_id_is_held_by_a_process_that_will_not_answer_is_stale()
	{
		var plan = Plan([Folder(100)], _ => SandboxProcess.Inaccessible);

		plan.Stale.ShouldBe([Folder(100)]);
	}

	/// <summary>
	/// The failure this exists to prevent. The process API answers a pid held by a protected service
	/// with an access-denied <see cref="Win32Exception"/>, and a guard around the whole loop turned that
	/// into a sweep that abandoned every folder after it, on every run. The host maps that exception
	/// now, which is exactly why the sweep must not depend on it: the next one it does not map arrives
	/// the same way.
	/// </summary>
	[Test]
	public void One_folder_whose_host_cannot_be_asked_about_does_not_stop_the_rest()
	{
		var plan = Plan(
			[Folder(100), Folder(200), Folder(300), Folder(400)],
			pid => pid == 200 ? throw new Win32Exception(5) : SandboxProcess.None);

		plan.Stale.ShouldBe([Folder(100), Folder(300), Folder(400)]);
		plan.Undecided.ShouldBe(1);
	}

	[Test]
	public void A_creation_time_that_cannot_be_read_keeps_only_that_folder()
	{
		var plan = Plan(
			[Folder(100), Folder(200)],
			_ => SandboxProcess.StartedAt(Made.AddMinutes(5)),
			folder => folder == Folder(100) ? throw new IOException("gone as it was read") : Made);

		plan.Stale.ShouldBe([Folder(200)]);
		plan.Undecided.ShouldBe(1);
	}

	/// <summary>
	/// A host deals with its own folder deliberately, and a folder not named for a pid is not one this
	/// sweep made. Neither is asked about, so neither can be counted either way.
	/// </summary>
	[Test]
	public void The_hosts_own_folder_and_one_not_named_for_a_pid_are_left_alone()
	{
		var plan = Plan(
			[Folder(OwnProcessId), Path.Combine("RoseMcpXaml", "notes")],
			pid => throw new InvalidOperationException($"asked about {pid}"));

		plan.Stale.ShouldBeEmpty();
		plan.Running.ShouldBe(0);
		plan.Undecided.ShouldBe(0);
	}

	/// <summary>
	/// The process API answers in local time and the file system in UTC, and comparing the two as they
	/// stand is off by the machine's offset: east of UTC that reads every running host as a reused id,
	/// which is the dangerous direction, and west of it reads a reused id as a running host. On a
	/// machine at UTC the two agree anyway, so this proves the comparison only where it matters.
	/// </summary>
	[Test]
	public void A_start_time_in_local_time_is_compared_as_the_instant_it_names()
	{
		var running = Plan([Folder(100)], _ => SandboxProcess.StartedAt(Made.AddSeconds(-2).ToLocalTime()));
		var reused = Plan([Folder(200)], _ => SandboxProcess.StartedAt(Made.AddMinutes(5).ToLocalTime()));

		running.Running.ShouldBe(1);
		reused.Stale.ShouldBe([Folder(200)]);
	}
}
