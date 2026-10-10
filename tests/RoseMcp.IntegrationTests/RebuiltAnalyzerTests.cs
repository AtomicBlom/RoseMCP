using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.BrokerHarness;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A generator rebuilt while its workspace is warm. The worker goes on generating from the build it loaded,
/// since an assembly cannot be unloaded, so what has to be true is that every read says so and names what
/// picks the new build up -- and that a new worker, started by <c>rose_workspace_reload</c> or by the idle
/// reload a person can turn on, does.
/// <para>
/// The generator is rebuilt for real, with a change to what it writes, so the old build and the new one are
/// told apart by their output rather than by a stamp the test arranged.
/// </para>
/// </summary>
public sealed class RebuiltAnalyzerTests
{
	private const string GeneratedHint = "Widget.Greeting.g.cs";
	private const string OldOutput = "from a source generator";
	private const string NewOutput = "from a rebuilt generator";

	/// <summary>Fast enough to finish in seconds; the quiet minute is crossed by moving the clock.</summary>
	private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(250);

	/// <summary>
	/// The barrier finds the rebuild by itself, on the next read, and says it on that read and every one
	/// after -- and does not reload the solution over it, which would load the same copy again.
	/// </summary>
	[Test]
	public async Task A_generator_rebuilt_mid_session_is_named_on_every_read_without_reloading_in_place()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		await using var session = await TestSession.OpenAsync(fixture);
		var before = await session.ReadAsync(cancellationToken);

		// Nothing is loaded, and so nothing is stale, until a generator has run.
		(await GeneratedDocumentService.ListAsync(before, null, cancellationToken)).Documents.Count.ShouldBe(2);
		before.Notices.ShouldNotContain(notice => notice.Contains("rebuilt", StringComparison.Ordinal));

		// A rebuilt file the barrier can only see by its stamp: the analyzer is not a document, and nothing
		// about the solution changes, so a reload would be the barrier mistaking it for a structural change.
		var generator = fixture.Path("WithGenerator", "Gen", "bin", "Debug", "netstandard2.0", "Gen.dll");
		await File.AppendAllTextAsync(generator, " ", cancellationToken);

		var after = await session.ReadAsync(cancellationToken);

		after.Revision.ShouldBe(before.Revision, "an analyzer changing is not a reason to reload the solution in place");
		ShouldNameTheRebuild(after.Notices);
		session.RebuiltAnalyzerPaths.ShouldHaveSingleItem().ShouldBe(generator, StringCompareShould.IgnoreCase);

		ShouldNameTheRebuild((await session.ReadAsync(cancellationToken)).Notices);
	}

	/// <summary>
	/// The whole path a person sees: an agent's read names the rebuild, status says it without calling the
	/// workspace degraded, the tray's row says it, and <c>rose_workspace_reload</c> starts a worker that
	/// generates from the new build and has nothing left to say. Off by default, the idle reload does nothing.
	/// </summary>
	[Test]
	public async Task A_rebuilt_generator_is_reported_and_a_reload_picks_it_up()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		(await GeneratedAsync(manager, hints, cancellationToken)).Text.ShouldContain(OldOutput);

		await RebuildGeneratorAsync(fixture, cancellationToken);

		var stale = await GeneratedAsync(manager, hints, cancellationToken);
		stale.Text.ShouldContain(OldOutput, customMessage: "the worker generates from the build it loaded");
		ShouldNameTheRebuild(stale.Notices);

		var status = await manager.StatusAsync(hints, cancellationToken);
		ShouldNameTheRebuild(status.Notices);
		status.State.ShouldBe(WorkspaceState.Loaded, "answers from an older analyzer are named, not untrusted");

		var worker = manager.Workers.ShouldHaveSingleItem();
		await WaitUntilAsync(() => worker.RebuiltAnalyzers.Count > 0, TimeSpan.FromSeconds(30), cancellationToken);
		ShouldNameTheRebuild(manager.Describe().ShouldHaveSingleItem().Notices);

		// Off unless chosen: a worker far past the quiet minute is left as it is.
		clock.Jump(TimeSpan.FromMinutes(10));
		await Task.Delay(SweepInterval * 6, cancellationToken);
		manager.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);
		worker.IsAlive.ShouldBeTrue();

		var reloaded = await manager.RestartAsync(hints, cancellationToken);
		reloaded.ShouldNotBeSameAs(worker);

		var fresh = await GeneratedAsync(manager, hints, cancellationToken);
		fresh.Text.ShouldContain(NewOutput);
		fresh.Notices.ShouldNotContain(notice => notice.Contains("rebuilt", StringComparison.Ordinal));
	}

	/// <summary>
	/// With the setting on, a worker holding a rebuilt generator is left alone while it is in use, and
	/// replaced once it has gone a minute unused, by a worker that generates from the new build -- with the
	/// reason in the new worker's history, for the person who finds it loading with nobody having asked.
	/// </summary>
	[Test]
	public async Task With_the_setting_on_an_idle_workspace_is_reloaded_onto_the_rebuilt_generator()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		using var fixture = FixtureSolution.Copy("WithGenerator", "WithGenerator.slnx");
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");

		var clock = new SteerableClock();
		await using var manager = CreateManager(configure: options =>
		{
			options.EvictionSweepInterval = SweepInterval;
			options.TimeProvider = clock;
			options.ReloadsRebuiltAnalyzersWhenIdle = () => true;
		});

		var hints = WorkspaceHints.From(RootedPath.Absolute(fixture.SolutionPath));

		(await GeneratedAsync(manager, hints, cancellationToken)).Text.ShouldContain(OldOutput);
		var worker = manager.Workers.ShouldHaveSingleItem();

		await RebuildGeneratorAsync(fixture, cancellationToken);
		ShouldNameTheRebuild((await GeneratedAsync(manager, hints, cancellationToken)).Notices);
		await WaitUntilAsync(() => worker.RebuiltAnalyzers.Count > 0, TimeSpan.FromSeconds(30), cancellationToken);

		// Within the minute it was last used, it is kept: an agent between two calls is not made to wait.
		await Task.Delay(SweepInterval * 6, cancellationToken);
		manager.Workers.ShouldHaveSingleItem().ShouldBeSameAs(worker);

		clock.Jump(TimeSpan.FromMinutes(2));
		await WaitUntilAsync(
			() => manager.Workers.SingleOrDefault() is { } current && !ReferenceEquals(current, worker),
			TimeSpan.FromSeconds(60),
			cancellationToken);

		worker.IsAlive.ShouldBeFalse();
		manager.Describe().ShouldHaveSingleItem().Recent
			.ShouldContain(activity => activity.Operation == WorkspaceManager.ReloadRebuiltOperation
				&& activity.Message != null
				&& activity.Message.Contains("Gen.dll", StringComparison.Ordinal));

		var fresh = await GeneratedAsync(manager, hints, cancellationToken);
		fresh.Text.ShouldContain(NewOutput);
		fresh.Notices.ShouldNotContain(notice => notice.Contains("rebuilt", StringComparison.Ordinal));
	}

	private static void ShouldNameTheRebuild(IReadOnlyList<string> notices) =>
		notices.ShouldContain(
			notice => notice.StartsWith("Gen.dll was rebuilt", StringComparison.Ordinal)
				&& notice.Contains("rose_workspace_reload", StringComparison.Ordinal),
			$"no notice named the rebuilt generator in: {string.Join(" | ", notices)}");

	private static Task<GeneratedDocumentContent> GeneratedAsync(
		WorkspaceManager manager,
		WorkspaceHints hints,
		CancellationToken cancellationToken) =>
		manager.CallAsync<GeneratedDocumentContent>(
			hints,
			ToolNames.ReadGeneratedDocument,
			new Dictionary<string, object?> { ["hintName"] = GeneratedHint },
			retryIfWorkerDied: true,
			cancellationToken);

	/// <summary>Changes what the generator writes, and builds it over the copy the worker loaded from.</summary>
	private static async Task RebuildGeneratorAsync(FixtureSolution fixture, CancellationToken cancellationToken)
	{
		var source = fixture.Path("WithGenerator", "Gen", "GreetingGenerator.cs");
		var text = await File.ReadAllTextAsync(source, cancellationToken);

		await File.WriteAllTextAsync(source, text.Replace(OldOutput, NewOutput, StringComparison.Ordinal), cancellationToken);
		fixture.Build("WithGenerator", "Gen", "Gen.csproj");
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + timeout;

		while (!condition())
		{
			if (DateTime.UtcNow > deadline) throw new TimeoutException($"The condition did not hold within {timeout}.");

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}
	}
}
