using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The activity log is what the tray window and GET /admin/workspaces read, so what matters is
/// that a row appears while work is in flight, says something true about it, and does not linger
/// once it is over.
/// </summary>
public sealed class ActivityLogTests
{
	private const string Solution = @"D:\somewhere\Thing.sln";

	[Test]
	public void Reports_an_operation_while_it_runs_and_files_it_when_it_ends()
	{
		var log = new ActivityLog();

		using (var scope = log.Begin(Solution, "rose_diagnostics", "solution"))
		{
			var running = log.Running(Solution).ShouldHaveSingleItem();

			running.Operation.ShouldBe("rose_diagnostics");
			running.Target.ShouldBe("solution");
			running.Outcome.ShouldBe(ActivityOutcome.Running);
			log.Recent(Solution).ShouldBeEmpty();

			scope.Report(new ProgressNotificationValue { Progress = 30, Total = 100, Message = "Analysing Core" });

			var reported = log.Running(Solution).ShouldHaveSingleItem();

			reported.Message.ShouldBe("Analysing Core");
			reported.PercentComplete.ShouldBe(30);
		}

		log.Running(Solution).ShouldBeEmpty();

		var finished = log.Recent(Solution).ShouldHaveSingleItem();

		finished.Outcome.ShouldBe(ActivityOutcome.Succeeded);
		finished.Error.ShouldBeNull();
	}

	/// <summary>
	/// A worker that stops knowing how far along it is must clear the number rather than leave a
	/// bar frozen at whatever it last said, which reads as a hang rather than as unknown.
	/// </summary>
	[Test]
	public void A_report_without_a_total_means_unknown_rather_than_unchanged()
	{
		var log = new ActivityLog();
		using var scope = log.Begin(Solution, "rose_find_references");

		scope.Report(new ProgressNotificationValue { Progress = 50, Total = 100, Message = "Loading" });
		scope.Report(new ProgressNotificationValue { Progress = 50, Message = "Searching the solution" });

		var running = log.Running(Solution).ShouldHaveSingleItem();

		running.PercentComplete.ShouldBeNull();
		running.Message.ShouldBe("Searching the solution");
	}

	/// <summary>Progress the client asked for still reaches the client, not just the tray.</summary>
	[Test]
	public void Passes_progress_on_to_the_calling_client()
	{
		var log = new ActivityLog();
		var upstream = new RecordingProgress();

		using var scope = log.Begin(Solution, "rose_rename_symbol", "Calculator.cs:7", upstream);
		scope.Report(new ProgressNotificationValue { Progress = 10, Total = 100, Message = "Renaming" });

		var forwarded = upstream.Values.ShouldHaveSingleItem();

		forwarded.Message.ShouldBe("Renaming");
		forwarded.Progress.ShouldBe(10);
	}

	[Test]
	public void Records_why_an_operation_failed()
	{
		var log = new ActivityLog();

		using (var scope = log.Begin(Solution, "rose_rename_symbol"))
		{
			scope.Complete(ActivityOutcome.Failed, "the worker died");
		}

		var finished = log.Recent(Solution).ShouldHaveSingleItem();

		// Disposing after a failure must not overwrite it with success.
		finished.Outcome.ShouldBe(ActivityOutcome.Failed);
		finished.Error.ShouldBe("the worker died");
	}

	/// <summary>
	/// This is live state for a window, not an audit trail. An agent making hundreds of calls must
	/// not grow the list without bound, and the newest are the ones worth showing.
	/// </summary>
	[Test]
	public void Keeps_only_the_last_few_finished_operations_newest_first()
	{
		var log = new ActivityLog();

		for (var index = 0; index < 12; index++)
		{
			log.Begin(Solution, $"call-{index}").Dispose();
		}

		var recent = log.Recent(Solution);

		recent.Count.ShouldBe(8);
		recent[0].Operation.ShouldBe("call-11");
		recent[^1].Operation.ShouldBe("call-4");
	}

	/// <summary>
	/// Closing a workspace takes its history with it. Attributing the old process's work to
	/// whatever starts next would be worse than showing nothing.
	/// </summary>
	[Test]
	public void Forgetting_a_workspace_drops_its_history()
	{
		var log = new ActivityLog();
		log.Begin(Solution, "rose_workspace_status").Dispose();

		log.Forget(Solution);

		log.Recent(Solution).ShouldBeEmpty();
		log.Running(Solution).ShouldBeEmpty();
	}

	/// <summary>
	/// GET /admin/workspaces is meant to return exactly what the tray window renders. It cannot do
	/// that while an outcome goes over the wire as "1", which is what the framework default gives.
	/// </summary>
	[Test]
	public void Serialises_an_outcome_as_a_word_rather_than_a_number()
	{
		var log = new ActivityLog();
		log.Begin(Solution, "rose_diagnostics", "solution").Dispose();

		var json = System.Text.Json.JsonSerializer.Serialize(
			new WorkspaceSummary
			{
				Workspace = Solution,
				DisplayName = "Thing",
				Alive = true,
				ExitReason = "Running",
				State = WorkspaceState.Loaded,
				StartedUtc = DateTime.UtcNow,
				Uptime = TimeSpan.FromMinutes(3),
				Recent = log.Recent(Solution),
			},
			ContractJson.Options);

		json.ShouldContain("\"outcome\":\"Succeeded\"", Case.Sensitive);
		json.ShouldContain("\"operation\":\"rose_diagnostics\"", Case.Sensitive);
	}

	private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
	{
		private readonly List<ProgressNotificationValue> _values = [];

		public IReadOnlyList<ProgressNotificationValue> Values
		{
			get
			{
				lock (_values)
				{
					return [.. _values];
				}
			}
		}

		public void Report(ProgressNotificationValue value)
		{
			lock (_values)
			{
				_values.Add(value);
			}
		}
	}
}
