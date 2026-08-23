using ModelContextProtocol;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// The activity log is what the tray window and GET /admin/workspaces read, so what matters is
/// that a row appears while work is in flight, says something true about it, and does not linger
/// once it is over.
/// </summary>
public sealed class ActivityLogTests
{
	private const string Solution = @"D:\somewhere\Thing.sln";

	[Fact]
	public void Reports_an_operation_while_it_runs_and_files_it_when_it_ends()
	{
		var log = new ActivityLog();

		using (var scope = log.Begin(Solution, "rose_diagnostics", "solution"))
		{
			var running = Assert.Single(log.Running(Solution));

			Assert.Equal("rose_diagnostics", running.Operation);
			Assert.Equal("solution", running.Target);
			Assert.Equal(ActivityOutcome.Running, running.Outcome);
			Assert.Empty(log.Recent(Solution));

			scope.Report(new ProgressNotificationValue { Progress = 30, Total = 100, Message = "Analysing Core" });

			var reported = Assert.Single(log.Running(Solution));

			Assert.Equal("Analysing Core", reported.Message);
			Assert.Equal(30, reported.PercentComplete);
		}

		Assert.Empty(log.Running(Solution));

		var finished = Assert.Single(log.Recent(Solution));

		Assert.Equal(ActivityOutcome.Succeeded, finished.Outcome);
		Assert.Null(finished.Error);
	}

	/// <summary>
	/// A worker that stops knowing how far along it is must clear the number rather than leave a
	/// bar frozen at whatever it last said, which reads as a hang rather than as unknown.
	/// </summary>
	[Fact]
	public void A_report_without_a_total_means_unknown_rather_than_unchanged()
	{
		var log = new ActivityLog();
		using var scope = log.Begin(Solution, "rose_find_references");

		scope.Report(new ProgressNotificationValue { Progress = 50, Total = 100, Message = "Loading" });
		scope.Report(new ProgressNotificationValue { Progress = 50, Message = "Searching the solution" });

		var running = Assert.Single(log.Running(Solution));

		Assert.Null(running.PercentComplete);
		Assert.Equal("Searching the solution", running.Message);
	}

	/// <summary>Progress the client asked for still reaches the client, not just the tray.</summary>
	[Fact]
	public void Passes_progress_on_to_the_calling_client()
	{
		var log = new ActivityLog();
		var upstream = new RecordingProgress();

		using var scope = log.Begin(Solution, "rose_rename_symbol", "Calculator.cs:7", upstream);
		scope.Report(new ProgressNotificationValue { Progress = 10, Total = 100, Message = "Renaming" });

		var forwarded = Assert.Single(upstream.Values);

		Assert.Equal("Renaming", forwarded.Message);
		Assert.Equal(10, forwarded.Progress);
	}

	[Fact]
	public void Records_why_an_operation_failed()
	{
		var log = new ActivityLog();

		using (var scope = log.Begin(Solution, "rose_rename_symbol"))
		{
			scope.Complete(ActivityOutcome.Failed, "the worker died");
		}

		var finished = Assert.Single(log.Recent(Solution));

		// Disposing after a failure must not overwrite it with success.
		Assert.Equal(ActivityOutcome.Failed, finished.Outcome);
		Assert.Equal("the worker died", finished.Error);
	}

	/// <summary>
	/// This is live state for a window, not an audit trail. An agent making hundreds of calls must
	/// not grow the list without bound, and the newest are the ones worth showing.
	/// </summary>
	[Fact]
	public void Keeps_only_the_last_few_finished_operations_newest_first()
	{
		var log = new ActivityLog();

		for (var index = 0; index < 12; index++)
		{
			log.Begin(Solution, $"call-{index}").Dispose();
		}

		var recent = log.Recent(Solution);

		Assert.Equal(8, recent.Count);
		Assert.Equal("call-11", recent[0].Operation);
		Assert.Equal("call-4", recent[^1].Operation);
	}

	/// <summary>
	/// Closing a workspace takes its history with it. Attributing the old process's work to
	/// whatever starts next would be worse than showing nothing.
	/// </summary>
	[Fact]
	public void Forgetting_a_workspace_drops_its_history()
	{
		var log = new ActivityLog();
		log.Begin(Solution, "rose_workspace_status").Dispose();

		log.Forget(Solution);

		Assert.Empty(log.Recent(Solution));
		Assert.Empty(log.Running(Solution));
	}

	/// <summary>
	/// GET /admin/workspaces is meant to return exactly what the tray window renders. It cannot do
	/// that while an outcome goes over the wire as "1", which is what the framework default gives.
	/// </summary>
	[Fact]
	public void Serialises_an_outcome_as_a_word_rather_than_a_number()
	{
		var log = new ActivityLog();
		log.Begin(Solution, "rose_diagnostics", "solution").Dispose();

		var json = System.Text.Json.JsonSerializer.Serialize(
			new WorkspaceSummary
			{
				SolutionPath = Solution,
				DisplayName = "Thing",
				Alive = true,
				ExitReason = "Running",
				StartedUtc = DateTime.UtcNow,
				Uptime = TimeSpan.FromMinutes(3),
				Recent = log.Recent(Solution),
			},
			ContractJson.Options);

		Assert.Contains("\"outcome\":\"Succeeded\"", json, StringComparison.Ordinal);
		Assert.Contains("\"operation\":\"rose_diagnostics\"", json, StringComparison.Ordinal);
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
