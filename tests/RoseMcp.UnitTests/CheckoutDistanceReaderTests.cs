using System.Collections.Concurrent;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// How far a local build's checkout has moved on, against a git that answers without a process.
/// <para>
/// What is worth testing here is what happens around git rather than git itself: that a failure is
/// a sentence rather than an exception, that a window polling hello does not start a git per poll,
/// and that a request giving up neither kills the count another request is waiting on nor leaves
/// one running past the reader's own disposal. The real process is driven by
/// <c>GitCommandTests</c> in the integration suite.
/// </para>
/// </summary>
public sealed class CheckoutDistanceReaderTests
{
	private const string Commit = "c11d75a16a82274f8c52a9e33110afee4bdecc0a";

	/// <summary>A build whose checkout is a directory that exists, so git is what decides.</summary>
	private static readonly BuildIdentity Local = new()
	{
		Version = "1.3.0",
		Commit = Commit,
		Dirty = false,
		Checkout = AppContext.BaseDirectory,
	};

	[Test]
	public async Task Counts_the_commits_head_and_origin_main_are_past_the_build()
	{
		var git = new ScriptedGit(arguments => arguments[^1].EndsWith("HEAD", StringComparison.Ordinal)
			? new GitOutcome { ExitCode = 0, Output = "3\n" }
			: new GitOutcome { ExitCode = 0, Output = "12\n" });
		using var reader = new CheckoutDistanceReader(Local, git, new SteppedClock());

		var distance = await reader.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		distance.ShouldNotBeNull();
		distance!.Checkout.ShouldBe(AppContext.BaseDirectory);
		distance.Head.ShouldBe(new CommitsPast { Ref = "HEAD", Count = 3 });
		distance.OriginMain.ShouldBe(new CommitsPast { Ref = "origin/main", Count = 12 });
		git.Calls.ShouldContain($"rev-list --count {Commit}..HEAD");
		git.Calls.ShouldContain($"rev-list --count {Commit}..origin/main");
	}

	/// <summary>
	/// A checkout with no <c>origin</c> still has a <c>HEAD</c>, so each count fails on its own,
	/// with git's own words for why.
	/// </summary>
	[Test]
	public async Task A_count_git_cannot_make_says_why_in_gits_words()
	{
		var git = new ScriptedGit(arguments => arguments[^1].EndsWith("HEAD", StringComparison.Ordinal)
			? new GitOutcome { ExitCode = 0, Output = "0" }
			: new GitOutcome { ExitCode = 128, Error = "fatal: ambiguous argument 'origin/main': unknown revision\nUse '--' to separate" });
		using var reader = new CheckoutDistanceReader(Local, git, new SteppedClock());

		var distance = await reader.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		distance!.Head.Count.ShouldBe(0);
		distance.OriginMain.Count.ShouldBeNull();
		distance.OriginMain.Unknown.ShouldBe(
			"Could not count the commits origin/main has past c11d75a: fatal: ambiguous argument 'origin/main': unknown revision");
	}

	/// <summary>A CI build names no checkout, so there is nothing to count from and git is never asked.</summary>
	[Test]
	public async Task A_build_with_no_checkout_has_no_distance()
	{
		var git = new ScriptedGit(_ => throw new InvalidOperationException("git should not run"));
		using var reader = new CheckoutDistanceReader(Local with { Checkout = null }, git, new SteppedClock());

		(await reader.ReadAsync(TestContext.Current!.Execution.CancellationToken)).ShouldBeNull();
		git.Calls.ShouldBeEmpty();
	}

	/// <summary>A checkout that has since been deleted is said, not asked of git in a directory that is gone.</summary>
	[Test]
	public async Task A_checkout_that_is_gone_is_said()
	{
		var gone = Path.Combine(Path.GetTempPath(), $"rose-gone-{Guid.NewGuid():N}");
		var git = new ScriptedGit(_ => throw new InvalidOperationException("git should not run"));
		using var reader = new CheckoutDistanceReader(Local with { Checkout = gone }, git, new SteppedClock());

		var distance = await reader.ReadAsync(TestContext.Current!.Execution.CancellationToken);

		distance!.Head.Unknown.ShouldNotBeNull();
		distance.Head.Unknown!.ShouldContain(gone, Case.Sensitive);
		distance.OriginMain.Unknown.ShouldBe(distance.Head.Unknown);
		git.Calls.ShouldBeEmpty();
	}

	/// <summary>
	/// A window polls hello. Within <see cref="CheckoutDistanceReader.Freshness"/> it gets the answer
	/// it got, and past it git is asked again, because the checkout moves.
	/// </summary>
	[Test]
	public async Task An_answer_is_kept_for_a_while_then_asked_again()
	{
		var git = new ScriptedGit(_ => new GitOutcome { ExitCode = 0, Output = "1" });
		var clock = new SteppedClock();
		using var reader = new CheckoutDistanceReader(Local, git, clock);
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await reader.ReadAsync(cancellationToken);
		clock.Advance(CheckoutDistanceReader.Freshness - TimeSpan.FromSeconds(1));
		await reader.ReadAsync(cancellationToken);

		git.Calls.Count.ShouldBe(2);

		clock.Advance(TimeSpan.FromSeconds(2));
		await reader.ReadAsync(cancellationToken);

		git.Calls.Count.ShouldBe(4);
	}

	/// <summary>
	/// A request that gives up stops waiting and nothing more: the count it started is the next
	/// request's answer, and cancelling it would hand that request a cancellation it never asked for.
	/// </summary>
	[Test]
	public async Task A_caller_giving_up_leaves_the_count_for_the_next_one()
	{
		var release = new TaskCompletionSource<GitOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		var git = new ScriptedGit((_, _) => release.Task);
		using var reader = new CheckoutDistanceReader(Local, git, new SteppedClock());

		using (var impatient = new CancellationTokenSource())
		{
			var first = reader.ReadAsync(impatient.Token);
			await impatient.CancelAsync();

			await Should.ThrowAsync<OperationCanceledException>(() => first);
		}

		var second = reader.ReadAsync(TestContext.Current!.Execution.CancellationToken);
		release.SetResult(new GitOutcome { ExitCode = 0, Output = "5" });

		(await second)!.Head.Count.ShouldBe(5);
	}

	/// <summary>
	/// Disposing the reader, which the host does as it stops, is what cancels a git still running,
	/// so a broker shutting down does not leave one behind; reading after that is refused.
	/// </summary>
	[Test]
	public async Task Disposing_the_reader_stops_a_count_in_flight()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var git = new ScriptedGit(async (_, token) =>
		{
			started.TrySetResult();

			try
			{
				await Task.Delay(Timeout.Infinite, token);
			}
			finally
			{
				stopped.TrySetResult();
			}

			return new GitOutcome();
		});
		var reader = new CheckoutDistanceReader(Local, git, new SteppedClock());
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		var reading = reader.ReadAsync(cancellationToken);
		await started.Task.WaitAsync(cancellationToken);

		reader.Dispose();

		await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
		await Should.ThrowAsync<OperationCanceledException>(() => reading);
		await Should.ThrowAsync<ObjectDisposedException>(() => reader.ReadAsync(cancellationToken));
	}

	/// <summary>A git that answers from a script, recording each command it was asked to run.</summary>
	private sealed class ScriptedGit(Func<IReadOnlyList<string>, CancellationToken, Task<GitOutcome>> answer) : GitCommand
	{
		public ScriptedGit(Func<IReadOnlyList<string>, GitOutcome> answer)
			: this((arguments, _) => Task.FromResult(answer(arguments)))
		{
		}

		private readonly ConcurrentQueue<string> _calls = new();

		public IReadOnlyCollection<string> Calls => _calls;

		public override Task<GitOutcome> RunAsync(
			string workingDirectory,
			IReadOnlyList<string> arguments,
			TimeSpan budget,
			CancellationToken cancellationToken)
		{
			_calls.Enqueue(string.Join(' ', arguments));

			return answer(arguments, cancellationToken);
		}
	}

	/// <summary>A clock that moves only when told to.</summary>
	private sealed class SteppedClock : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}
}
