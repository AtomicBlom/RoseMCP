using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The pieces of the operator views that are rules rather than reads: what a workspace was loaded under, and what
/// the machine's memory is on Linux.
/// </summary>
public sealed class OperatorViewsTests
{
	/// <summary>
	/// The configuration and platform come from what the worker reported loading, extra properties and all, and
	/// from what it was started with only until it reports.
	/// </summary>
	[Test]
	public void A_workspace_is_loaded_under_what_its_worker_reported_and_until_then_what_it_was_started_with()
	{
		var started = new WorkspaceBuildOverrides { Configuration = "Debug-2027", Platform = "x64" };

		OperatorViews.Loaded("Debug|x64", started: null).ShouldBe(("Debug", "x64"));
		OperatorViews.Loaded("Release|AnyCPU (RevitVersion=2027, Flavor=a|b)", started: null).ShouldBe(("Release", "AnyCPU"));
		OperatorViews.Loaded("Debug-2027|x64", started).ShouldBe(("Debug-2027", "x64"));
		OperatorViews.Loaded(null, started).ShouldBe(("Debug-2027", "x64"), "nothing reported yet");
		OperatorViews.Loaded(null, started: null).ShouldBe((null, null), "nothing reported and nothing asked for");
	}

	[Test]
	public void The_machines_memory_is_read_from_meminfo_with_what_the_kernel_can_give_back()
	{
		string[] meminfo =
		[
			"MemTotal:       32768000 kB",
			"MemFree:         1024000 kB",
			"MemAvailable:   20480000 kB",
			"Buffers:          512000 kB",
		];

		var machine = MachineMemoryProbe.ParseMeminfo(meminfo).ShouldNotBeNull();
		machine.TotalBytes.ShouldBe(32768000L * 1024);
		machine.AvailableBytes.ShouldBe(20480000L * 1024, "MemAvailable counts the cache, which MemFree does not");

		MachineMemoryProbe.ParseMeminfo(["MemTotal: 1000 kB", "MemFree: 10 kB"]).ShouldNotBeNull().AvailableBytes
			.ShouldBeNull("an old kernel says nothing about what is free");
		MachineMemoryProbe.ParseMeminfo(["Garbage"]).ShouldBeNull();
	}

	/// <summary>Whatever the platform, this machine's memory is read: Windows asks the system, Linux reads meminfo.</summary>
	[Test]
	public void This_machines_memory_is_read()
	{
		var machine = MachineMemoryProbe.Read().ShouldNotBeNull();

		machine.TotalBytes.ShouldBeGreaterThan(0);
		if (machine.AvailableBytes is { } available) available.ShouldBeInRange(0, machine.TotalBytes);
	}
}
