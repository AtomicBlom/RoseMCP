namespace RoseMcp.IntegrationTests;

/// <summary>
/// One fixture, copied once and loaded once, for every test in the run that only reads it.
/// <para>
/// A load is a design-time build, and the read a test makes of it costs a fraction of one. A read takes
/// its session as pure input, so one load serves every reader, and concurrent reads of one session are
/// what <see cref="WorkspaceSession"/> is built for. Why, and what cannot share, is in
/// docs/decisions/a-test-that-only-reads-shares-its-workspace.md.
/// </para>
/// <para>
/// Nothing that writes can share it, and three things keep it that way. The session is never handed
/// out, only <see cref="ReadAsync"/>, so no tool can write through it. The copy's files are read-only
/// on disk, so an edit behind its back fails in the test that made it. And a new file is the one
/// write neither of those stops, so every read checks the workspace is still the one that loaded and
/// refuses to answer from one that moved -- failing whichever test read next, which is why the other
/// two exist. A test that writes opens its own with <see cref="TestSession.OpenAsync(FixtureSolution, TimeSpan?, ShadowCopyAnalyzerAssemblyLoader?)"/>.
/// </para>
/// <para>
/// Lazy, as the probe apps are: an object shared across the assembly is constructed before any test
/// runs, so copying or loading in the constructor would make every filtered run pay for fixtures it
/// never reads. Nothing happens until a test asks.
/// </para>
/// <para>
/// The load belongs to no test. It runs on this object's own token, which only disposal cancels, and
/// without the first caller's execution context, so a test that times out abandons its own wait rather
/// than the load everyone else is waiting on, and nothing the session does afterwards is attributed to
/// whichever test happened to ask first. A load that fails is not tried again: every reader is told
/// what the one attempt hit, so a broken fixture costs one load rather than one per test.
/// </para>
/// </summary>
public sealed class SharedWorkspace : IAsyncDisposable
{
	private readonly string _fixtureName;
	private readonly string _solutionFileName;
	private readonly Lock _gate = new();
	private readonly CancellationTokenSource _disposing = new();

	private FixtureSolution? _fixture;
	private Task<Loaded>? _load;
	private bool _disposed;

	internal SharedWorkspace(string fixtureName, string solutionFileName)
	{
		_fixtureName = fixtureName;
		_solutionFileName = solutionFileName;
	}

	/// <summary>The solution file inside the shared copy.</summary>
	public string SolutionPath => Fixture().SolutionPath;

	/// <summary>A path inside the shared copy, as <see cref="FixtureSolution.Path"/> gives one.</summary>
	public string Path(params string[] parts) => Fixture().Path(parts);

	/// <summary>
	/// A snapshot of the shared workspace, loading it first if no test has yet. Throws rather than
	/// answer from a workspace that has moved since it loaded, since every test reading it relies on
	/// it being exactly the fixture.
	/// </summary>
	public async Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
	{
		var loaded = await LoadAsync(cancellationToken);
		var snapshot = await loaded.Session.ReadAsync(cancellationToken);

		var moved = snapshot.Stale || snapshot.Revision != loaded.Revision || snapshot.Notices.Count > 0;
		if (moved)
		{
			throw new InvalidOperationException(
				$"The shared {_fixtureName} workspace is no longer the one that loaded (revision {loaded.Revision} then, "
					+ $"{snapshot.Revision} now), so something wrote to a fixture that every reading test relies on. A test "
					+ "that writes opens a session of its own with TestSession.OpenAsync. The read said: "
					+ string.Join(" | ", snapshot.Notices));
		}

		return snapshot;
	}

	private async Task<Loaded> LoadAsync(CancellationToken cancellationToken)
	{
		Task<Loaded> load;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			if (_load is null)
			{
				var fixture = FixtureLocked();
				var token = _disposing.Token;

				using (ExecutionContext.SuppressFlow())
				{
					_load = Task.Run(() => OpenAsync(fixture, token));
				}
			}

			load = _load;
		}

		try
		{
			return await load.WaitAsync(cancellationToken);
		}
		catch (Exception exception) when (load.IsFaulted)
		{
			throw new InvalidOperationException(
				$"The shared {_fixtureName} workspace could not be loaded, and this is what its one load hit.",
				exception);
		}
	}

	private static async Task<Loaded> OpenAsync(FixtureSolution fixture, CancellationToken cancellationToken)
	{
		var session = await TestSession.OpenAsync(fixture, unloadGrace: null, analyzerLoader: null, cancellationToken);

		return new Loaded(session, session.Revision);
	}

	private FixtureSolution Fixture()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			return FixtureLocked();
		}
	}

	/// <summary>The copy, made on first use and made read-only before anything loads it.</summary>
	private FixtureSolution FixtureLocked()
	{
		if (_fixture is not null) return _fixture;

		var fixture = FixtureSolution.Copy(_fixtureName, _solutionFileName);
		SetReadOnly(fixture.Root, readOnly: true);

		return _fixture = fixture;
	}

	private static void SetReadOnly(string root, bool readOnly)
	{
		foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			var attributes = File.GetAttributes(file);
			File.SetAttributes(file, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
		}
	}

	/// <summary>
	/// Ends the session before deleting the copy it watches. Only runs once every test sharing this
	/// has finished, so no read is in flight; a load still running is cancelled and waited for, since
	/// its session would otherwise outlive the directory it is watching.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		Task<Loaded>? load;
		FixtureSolution? fixture;
		lock (_gate)
		{
			if (_disposed) return;

			_disposed = true;
			load = _load;
			fixture = _fixture;
		}

		// Each step is attempted whatever the one before it did: a session that fails to close still has
		// its copy deleted, and a copy whose attributes cannot all be cleared is still deleted as far as
		// it can be.
		try
		{
			await _disposing.CancelAsync();
			await DisposeSessionAsync(load);
		}
		finally
		{
			try
			{
				if (fixture is not null) SetReadOnly(fixture.Root, readOnly: false);
			}
			finally
			{
				fixture?.Dispose();
				_disposing.Dispose();
			}
		}
	}

	private static async Task DisposeSessionAsync(Task<Loaded>? load)
	{
		if (load is null) return;

		WorkspaceSession session;
		try
		{
			session = (await load).Session;
		}
		catch (Exception)
		{
			// A load that failed or was cancelled leaves no session behind, and every test that read
			// was told why.
			return;
		}

		await session.DisposeAsync();
	}

	/// <summary>The session, and the revision it loaded at, which every later read must still be at.</summary>
	private sealed record Loaded(WorkspaceSession Session, long Revision);
}
