using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.UnitTests;

/// <summary>
/// Whether the watcher can tell its own worker's writes from somebody else's.
/// <para>
/// One rewrite raises more than one event, so suppression that forgets the path on the first one
/// leaks the rest. Those count toward the bulk-change threshold and force a full reload of every
/// project, which is the opposite of what noting a self-write is for. The two tests here are a
/// pair: the second is what stops the first passing because no event ever arrived.
/// </para>
/// </summary>
public sealed class SolutionWatcherTests
{
	/// <summary>
	/// Long enough for a write's events to arrive, and paid in full by the suppression test, which
	/// asserts an absence and so cannot stop early.
	/// </summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

	[Test]
	public async Task A_noted_write_raises_no_file_change_however_many_events_it_makes()
	{
		using var tree = WatchedTree.Create();

		tree.Watcher.NoteSelfWrite(tree.SourcePath);
		await tree.RewriteAsync();
		await Task.Delay(Settle, TestContext.Current!.Execution.CancellationToken);

		Assert.False(
			tree.Watcher.Drain().Signal.HasFlag(WatchSignal.FileChanges),
			"one NoteSelfWrite has to cover every event that one write raises");
	}

	/// <summary>
	/// The same write, unannounced, and this one must be seen. Without it the test above passes on
	/// a watcher that reports nothing at all, which is exactly the shape of a test that looks like
	/// coverage and is not.
	/// </summary>
	[Test]
	public async Task An_unannounced_write_is_reported()
	{
		using var tree = WatchedTree.Create();

		await tree.RewriteAsync();

		Assert.True(
			await tree.WaitForFileChangeAsync(),
			"an edit nobody announced is what the watcher exists to notice");
	}

	/// <summary>A watched directory holding a solution file and one source file.</summary>
	private sealed class WatchedTree : IDisposable
	{
		private readonly string _root;

		private WatchedTree(string root, string sourcePath, SolutionWatcher watcher)
		{
			_root = root;
			SourcePath = sourcePath;
			Watcher = watcher;
		}

		public string SourcePath { get; }

		public SolutionWatcher Watcher { get; }

		public static WatchedTree Create()
		{
			var root = Directory.CreateTempSubdirectory("rosemcp-watch-").FullName;
			var solutionPath = Path.Combine(root, "Watched.sln");
			var sourcePath = Path.Combine(root, "Class.cs");

			File.WriteAllText(solutionPath, string.Empty);
			File.WriteAllText(sourcePath, "class C { }");

			var watcher = new SolutionWatcher(solutionPath, NullLogger<SolutionWatcher>.Instance);

			// Staging the layout raises events of its own, and they belong to nobody's test.
			watcher.Drain();

			return new WatchedTree(root, sourcePath, watcher);
		}

		/// <summary>Rewrites the source longer than it was, so size and write time both move.</summary>
		public Task RewriteAsync() =>
			File.WriteAllTextAsync(SourcePath, "class C { void M() { } } // rewritten, and longer than before");

		/// <summary>
		/// Waits for a file change, giving up rather than hanging. A fixture check with no bound
		/// turns a failing test into a wedged suite.
		/// </summary>
		public async Task<bool> WaitForFileChangeAsync()
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

			while (DateTime.UtcNow < deadline)
			{
				if (Watcher.Drain().Signal.HasFlag(WatchSignal.FileChanges)) return true;

				await Task.Delay(25);
			}

			return false;
		}

		public void Dispose()
		{
			Watcher.Dispose();
			Directory.Delete(_root, recursive: true);
		}
	}
}
