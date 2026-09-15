using Microsoft.Extensions.Logging.Abstractions;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the watcher hears, and what it keeps.
/// <para>
/// One rewrite raises more than one event, so suppression that forgets the path on the first one leaks
/// the rest back as somebody else's edits. The first two tests here are a pair: the second is what stops
/// the first passing because no event ever arrived. The rest pin down what the watcher remembers, which
/// is build files and nothing else, so no quantity of source changes can turn into a reload.
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

	/// <summary>
	/// A burst of source files well past the size of any batch a watcher might be tempted to call "too
	/// much". Every read stats and walks for source files itself, so the burst is file changes and nothing
	/// more: no full resync, and no build files remembered.
	/// </summary>
	[Test]
	public async Task A_burst_of_source_files_is_file_changes_and_never_a_full_resync()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = WatchedTree.Create();

		for (var index = 0; index < 120; index++)
		{
			await File.WriteAllTextAsync(tree.PathTo($"Generated{index}.cs"), $"class G{index} {{ }}", token);
		}

		await Task.Delay(Settle, token);
		var report = tree.Watcher.Drain();

		Assert.True(report.HasFlag(WatchSignal.FileChanges), "the burst has to have been heard for the rest to mean anything");
		Assert.False(report.HasFlag(WatchSignal.FullResyncRequired), "how many source files changed is never a reason to reload");
		Assert.Empty(report.BuildFilesAppeared);
		Assert.Empty(report.BuildFilesChanged);
	}

	/// <summary>
	/// A build file is remembered by what happened to it -- appearing, then changing -- and a source file
	/// written beside it is not remembered at all.
	/// </summary>
	[Test]
	public async Task A_build_file_is_reported_as_appeared_then_changed_and_a_source_file_is_not()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = WatchedTree.Create();
		var props = tree.PathTo("Directory.Build.props");

		await File.WriteAllTextAsync(props, "<Project />", token);
		await File.WriteAllTextAsync(tree.PathTo("Other.cs"), "class O { }", token);
		await Task.Delay(Settle, token);

		var first = tree.Watcher.Drain();
		Assert.Contains(first.BuildFilesAppeared, path => path.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(first.BuildFilesAppeared, path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

		await File.WriteAllTextAsync(props, "<Project><PropertyGroup /></Project>", token);
		await Task.Delay(Settle, token);

		var second = tree.Watcher.Drain();
		Assert.Contains(second.BuildFilesChanged, path => path.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase));
		Assert.Empty(second.BuildFilesAppeared);
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

		/// <summary>A path inside the watched directory.</summary>
		public string PathTo(string name) => Path.Combine(_root, name);

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
