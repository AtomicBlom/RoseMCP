using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// One of a stopped target's managed threads, as the inspector shows it.
/// <para>
/// A thread list is what tells a deadlock from a slow method, so the parts asserted here are the
/// ones that carry that: what the runtime says each thread is doing, and which one the stop is on.
/// </para>
/// </summary>
public sealed class ThreadRowTests
{
	[Test]
	public void A_thread_reads_as_its_id_state_and_top_frame()
	{
		var row = new ThreadRow(Thread(4128, ["Background", "WaitSleepJoin"], "MyApp.Worker.Pump"));

		row.Id.ShouldBe(4128);
		row.IdLabel.ShouldBe("4128");
		row.State.ShouldBe($"Background{Format.Separator}WaitSleepJoin");
		row.HasState.ShouldBeTrue();
		row.TopFrame.ShouldBe("MyApp.Worker.Pump");
	}

	/// <summary>
	/// A thread with no managed frame is ordinary -- the finalizer and the pool's waiters are always
	/// in that state -- so it is said in words. An empty cell reads as a thread the debugger tried to
	/// walk and could not, which is a different claim and a worrying one.
	/// </summary>
	[Test]
	public void A_thread_with_no_managed_frame_says_so()
	{
		var row = new ThreadRow(Thread(9004, ["Background"], topFrame: null));

		row.TopFrame.ShouldBe("no managed frame");
	}

	/// <summary>
	/// A runtime that says nothing about a thread leaves the line off rather than printing an empty
	/// one: running managed code is the state that has no flags, and it is the commonest.
	/// </summary>
	[Test]
	public void A_thread_the_runtime_says_nothing_about_has_no_state_line()
	{
		var row = new ThreadRow(Thread(1, [], "MyApp.Program.Main"));

		row.State.ShouldBe(string.Empty);
		row.HasState.ShouldBeFalse();
	}

	/// <summary>The thread the stop is on is marked, because it is the one the stack pane is about.</summary>
	[Test]
	public void The_thread_the_stop_is_on_is_marked()
	{
		new ThreadRow(Thread(1, [], "MyApp.Program.Main", stopped: true)).IsStopped.ShouldBeTrue();
		new ThreadRow(Thread(2, [], "MyApp.Worker.Pump")).IsStopped.ShouldBeFalse();
	}

	private static LiveThread Thread(
		int id,
		string[] userState,
		string? topFrame,
		bool stopped = false) =>
		new()
		{
			Id = id,
			UserState = userState,
			IsStopped = stopped,
			TopFrame = topFrame,
		};
}
