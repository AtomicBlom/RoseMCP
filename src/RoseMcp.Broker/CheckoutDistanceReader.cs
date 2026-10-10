using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// How far the checkout a local build was made in has moved on since: how many commits its
/// <c>HEAD</c> and its <c>origin/main</c> are past the running commit. What a person running their
/// own build wants to know is whether it is the code in front of them, and the build cannot answer
/// that from what it was stamped with, because the checkout went on without it.
/// <para>
/// Asked of git, so it is kept off every path that answers an agent: only <c>/operator/hello</c>
/// reads it. It is computed on first read and kept for <see cref="Freshness"/>, so a window polling
/// hello starts one pair of git processes a half-minute at most, and concurrent reads share one.
/// </para>
/// <para>
/// The git work belongs to this reader rather than to the request that started it. A request that
/// gives up stops waiting and leaves it to finish for the next one; disposing the reader, which the
/// host does as it stops, is what kills a git still running. Each command has its own budget, so
/// neither a hung git nor a reader nobody disposes holds a process for longer than that.
/// </para>
/// </summary>
public sealed class CheckoutDistanceReader : IDisposable
{
	/// <summary>How long one answer is served before git is asked again.</summary>
	public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

	private const string Head = "HEAD";
	private const string OriginMain = "origin/main";

	private readonly GitCommand _git;
	private readonly TimeProvider _time;
	private readonly TimeSpan _budget;
	private readonly CancellationTokenSource _stopping = new();
	private readonly Lock _gate = new();

	private Task<CheckoutDistance>? _reading;
	private DateTimeOffset _readAt;
	private bool _disposed;

	/// <param name="build">The running build, whose checkout and commit are counted from.</param>
	/// <param name="git">What runs git; a test passes one that answers without a process.</param>
	/// <param name="time">The clock <see cref="Freshness"/> is measured on.</param>
	/// <param name="budget">How long each git command gets; <see cref="GitCommand.DefaultBudget"/> where null.</param>
	public CheckoutDistanceReader(BuildIdentity build, GitCommand git, TimeProvider time, TimeSpan? budget = null)
	{
		Build = build;
		_git = git;
		_time = time;
		_budget = budget ?? GitCommand.DefaultBudget;
	}

	/// <summary>The build this reader counts from.</summary>
	public BuildIdentity Build { get; }

	/// <summary>The reader for the broker's own build, with a real git.</summary>
	public static CheckoutDistanceReader ForBroker() =>
		new(BuildIdentity.Of(typeof(CheckoutDistanceReader).Assembly), new GitCommand(), TimeProvider.System);

	/// <summary>
	/// The distance, or null for a build that names no checkout or no commit -- a CI build, or one
	/// made outside git -- which has nothing to count from.
	/// </summary>
	/// <param name="cancellationToken">Abandons this wait only; the count goes on for the next reader.</param>
	public async Task<CheckoutDistance?> ReadAsync(CancellationToken cancellationToken)
	{
		var countable = Build.Checkout is not null && Build.Commit is not null;
		if (!countable) return null;

		Task<CheckoutDistance> reading;

		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			var now = _time.GetUtcNow();
			var finished = _reading is { IsCompleted: true };
			var failed = _reading is { IsCompletedSuccessfully: false, IsCompleted: true };
			var stale = _reading is null || failed || (finished && now - _readAt >= Freshness);

			if (stale)
			{
				_readAt = now;

				// Detached, because the count outlives the request that started it and is not that
				// request's work: its log lines belong to no call.
				_reading = Detached.Run(() => CountAsync(Build.Checkout!, Build.Commit!, _stopping.Token));
			}

			reading = _reading!;
		}

		return await reading.WaitAsync(cancellationToken);
	}

	/// <summary>Kills any git still running and refuses further reads.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;

			_disposed = true;
		}

		_stopping.Cancel();
		_stopping.Dispose();
	}

	private async Task<CheckoutDistance> CountAsync(string checkout, string commit, CancellationToken stopping)
	{
		if (!Directory.Exists(checkout))
		{
			var gone = $"The checkout this build was made in, {checkout}, is not there any more.";

			return new CheckoutDistance
			{
				Checkout = checkout,
				Head = new CommitsPast { Ref = Head, Unknown = gone },
				OriginMain = new CommitsPast { Ref = OriginMain, Unknown = gone },
				CheckedUtc = _time.GetUtcNow(),
			};
		}

		var head = await CountPastAsync(checkout, commit, Head, stopping);
		var originMain = await CountPastAsync(checkout, commit, OriginMain, stopping);

		return new CheckoutDistance
		{
			Checkout = checkout,
			Head = head,
			OriginMain = originMain,
			CheckedUtc = _time.GetUtcNow(),
		};
	}

	/// <summary>
	/// Commits reachable from <paramref name="reference"/> and not from <paramref name="commit"/>:
	/// what the checkout has that the running build does not. A commit rebased out of the checkout,
	/// or a checkout with no <c>origin</c>, is said in git's own words.
	/// </summary>
	private async Task<CommitsPast> CountPastAsync(string checkout, string commit, string reference, CancellationToken stopping)
	{
		var outcome = await _git.RunAsync(checkout, ["rev-list", "--count", $"{commit}..{reference}"], _budget, stopping);

		var parsed = int.TryParse(outcome.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count);
		var counted = outcome.Succeeded && parsed;

		if (counted) return new CommitsPast { Ref = reference, Count = count };

		var said = outcome.Error
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault();

		var why = said ?? $"git exited with {outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "no code"} and said nothing";

		return new CommitsPast
		{
			Ref = reference,
			Unknown = $"Could not count the commits {reference} has past {commit[..Math.Min(7, commit.Length)]}: {why}",
		};
	}
}
