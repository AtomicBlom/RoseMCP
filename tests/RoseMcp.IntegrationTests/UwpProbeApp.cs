using System.Diagnostics;
using System.Xml.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The classic UWP probe app, built, staged and registered once for the whole test run.
/// <para>
/// It used to be all of that per test, twenty times over: a vswhere probe, a PowerShell-driven MSVC
/// build of the native provider, an <c>msbuild -t:Restore</c> and an <c>msbuild -t:Build</c> of the
/// app, a layout stage that deleted and re-copied every packaged file, an <c>Add-AppxPackage</c>, and
/// a <c>Remove-AppxPackage</c> on the way out. All of it produces byte-identical output every time,
/// and all of it ran inside one test class -- which is one xUnit collection, so it ran serially. That
/// made the twenty UWP tests, rather than the hundred and sixty Roslyn solution loads, the thing the
/// suite's wall clock was actually made of.
/// </para>
/// <para>
/// The existing x64-host cache in <see cref="TestToolchain.EnsureX64Build"/> is the precedent, and its
/// reasoning carries: MSBuild is incremental, so paying for the check once a run costs almost nothing,
/// and paying for it once means it is still a real build rather than a stale exe being "found".
/// </para>
/// <para>
/// Lazy on purpose. An assembly fixture is constructed before any test in the assembly runs, so doing
/// the work in a constructor or <c>InitializeAsync</c> would make every filtered run of one Roslyn
/// test pay for a UWP toolchain build. Nothing here happens until a UWP test asks for an AUMID.
/// </para>
/// </summary>
public sealed class UwpProbeApp : IAsyncDisposable
{
	private const string PackageName = "RoseMcp.ProbeApp.UwpClassic";

	/// <summary>The layout's executable, which is also its process name -- what <see cref="StopApp"/> kills.</summary>
	private const string ProcessName = "Rose.ProbeApp.UwpClassic";

	private readonly Lock _gate = new();

	/// <summary>
	/// One UWP test at a time. Held here rather than by disabling parallelization on the test class,
	/// which sounds like the same thing and is not: that stops the class running in parallel with
	/// <em>anything</em>, and it was measured -- one of two hundred and ten other tests overlapped a
	/// live-app test, so the suite's two halves added up (268s + 109s) instead of overlapping. What
	/// actually cannot overlap is two tests driving this one app, so that is what is serialised.
	/// </summary>
	private readonly SemaphoreSlim _oneAtATime = new(1, 1);

	private bool _msBuildProbed;
	private string? _msBuild;
	private bool _providerProbed;
	private bool _providerBuilt;
	private bool _registered;
	private string? _aumid;
	private string? _layoutDirectory;

	/// <summary>
	/// The AUMID of a registered, launchable probe app, having built everything it needs. Skips the
	/// calling test where the environment cannot provide it, which is the same three skips these tests
	/// each spelled out for themselves.
	/// </summary>
	/// <param name="needsXamlProvider">
	/// True for the tests that go on to read or edit the visual tree, which need the native provider
	/// as well as the app. False for the two that only launch and debug it -- a machine with the UWP
	/// tooling but no C++ toolset should still run those.
	/// </param>
	private string AumidCore(bool needsXamlProvider)
	{
		lock (_gate)
		{
			var msbuild = MsBuild();
			if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");

			if (needsXamlProvider && !ProviderBuilt())
			{
				Assert.Skip("The native XAML provider could not be built (no C++ toolset).");
			}

			// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
			EnsureX64HostBuilt();

			if (_registered)
			{
				if (_aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");
				return _aumid!;
			}

			_registered = true;
			_layoutDirectory = Stage(Build(msbuild!));
			_aumid = Register(_layoutDirectory);

			if (_aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");
			return _aumid!;
		}
	}

	/// <summary>
	/// The staged AppX layout the package is registered from, which is also the install location a
	/// UWP session reports. Null until <see cref="LeaseAsync"/> has staged it.
	/// </summary>
	public string? LayoutDirectory => _layoutDirectory;

	/// <summary>
	/// Takes the app for one test: waits its turn, makes sure everything is built and registered, and
	/// hands back the AUMID to launch. Disposing the lease ends the app and lets the next test in.
	/// <para>
	/// A lease rather than a getter plus a <c>finally</c>, because the two have to go together. The
	/// turn is only safely held while the app is nobody else's, and the thing that ends the app is the
	/// thing that ends the turn.
	/// </para>
	/// </summary>
	/// <remarks>
	/// The unconverted shape, kept while tests move to the phases one at a time. It goes through the
	/// same gate as everything else, and that is not tidiness: a lease of its own would be a second
	/// lock over one single-instance app, so an old-style test and a phase B test would each believe
	/// they had it. That is exactly what happened -- a run wedged with the host alive and the app gone,
	/// because one test launched its own instance while another was using the shared one.
	/// </remarks>
	public async Task<Lease> LeaseAsync(bool needsXamlProvider, CancellationToken cancellationToken)
	{
		var aumid = await EnterAsync(writer: true, needsXamlProvider, cancellationToken);

		// It launches its own app, so the shared one has to go first, exactly as phase A does.
		await CloseSharedAsync();
		await StopAppAndWaitAsync();

		return new Lease(this, aumid);
	}

	/// <summary>
	/// One test's turn with the app. Disposing it ends the app and then releases the turn, in that
	/// order: the next test launches by AUMID, and a surviving instance would be activated rather
	/// than started under the debugger, so the turn must not be handed on while the app is still up.
	/// <para>
	/// Each test also stops the app in its own <c>finally</c>, which is what ends it promptly rather
	/// than at scope exit. Stopping is idempotent, so this is the backstop for the case that finally
	/// cannot cover: a test written without one.
	/// </para>
	/// </summary>
	public sealed class Lease(UwpProbeApp probe, string aumid) : IDisposable
	{
		public string Aumid { get; } = aumid;

		public void Dispose()
		{
			probe.StopApp();
			probe._phases.ReleaseWriter();
		}
	}

	// ---- The shared app, and the three ways a test can ask for it -------------------------------
	//
	// A launch costs about 6.5 seconds and the XAML work in a test costs about 1.2, so twenty tests
	// launching twenty apps spend nearly all of their time getting to the point where they can begin.
	// Sharing one launched app is what makes a new test cost what its own work costs. What each phase
	// gives up in exchange is different, and worth being precise about, because "isolation" as a
	// single property is not what is being traded:
	//
	//   TakeAppAsync   the app to yourself, launched by you. For tests *about* launching and
	//                  attaching, where a fresh process is the thing under test. Tears the shared
	//                  session down first, since a packaged app is single-instance.
	//   TakeSessionAsync  the shared app to yourself. For state that is global to the app and has no
	//                  smaller owner: the selection, select mode, the resource dictionary. Serial by
	//                  construction, and required to hand the app back as it was found.
	//   TakeSlotAsync  the shared app, plus an empty named container nobody else will touch. For
	//                  tests that build elements, work on them and take them away. These may overlap
	//                  each other.
	//
	// Overlapping is allowed for slots rather than pursued: every XAML request through one host is
	// serialised behind XamlDiagnosticsSession's lock (#93), and behind that a single UI thread, so
	// two slot tests cannot have their XAML work run at the same time however they are scheduled.
	// What slots actually buy is that sharing the app is *safe*, which is the thing that makes a new
	// test cheap.

	/// <summary>How many slots the probe's markup declares. Named Slot0..Slot15 under Scratch.</summary>
	private const int SlotCount = 16;

	private readonly PhaseGate _phases = new();
	private readonly Stack<int> _freeSlots = new(Enumerable.Range(0, SlotCount).Reverse());

	/// <summary>
	/// Held while the shared app is being brought up, because bringing it up is the one thing readers
	/// do that is not read-only. Phase C tests run together on purpose, and each of them asks for the
	/// shared session -- so after a phase A test has ended the app, several of them arrive at once,
	/// each correctly sees no app running, and each starts by ending the app before launching it.
	/// The second one kills what the first has just launched. Whoever loses that race holds a session
	/// whose process is gone, and the next XAML call spends twenty seconds discovering it and reports
	/// "the target's XAML diagnostics endpoint did not appear", which describes the corpse rather than
	/// the killing. The phase gate cannot cover this: readers are meant to overlap, and the relaunch
	/// is the exception hiding among them.
	/// </summary>
	private readonly SemaphoreSlim _relaunch = new(1, 1);

	private LiveAppSessionManager? _sharedManager;
	private LiveAppSession? _sharedSession;

	/// <summary>
	/// The app to yourself, launched by the caller (phase A). The shared session is closed first,
	/// because a packaged app is single-instance and the caller is about to start one.
	/// </summary>
	public async Task<AppTurn> TakeAppAsync(bool needsXamlProvider, CancellationToken cancellationToken)
	{
		var aumid = await EnterAsync(writer: true, needsXamlProvider, cancellationToken);
		await CloseSharedAsync();
		await StopAppAndWaitAsync();

		return new AppTurn(this, aumid);
	}

	/// <summary>
	/// The shared app to yourself (phase B), for state with no owner smaller than the app. Releasing
	/// the turn checks the app was handed back in the state it was found in.
	/// </summary>
	public async Task<SessionTurn> TakeSessionAsync(CancellationToken cancellationToken)
	{
		await EnterAsync(writer: true, needsXamlProvider: true, cancellationToken);

		return new SessionTurn(this, await SharedSessionAsync(cancellationToken));
	}

	/// <summary>
	/// The shared app plus a slot of your own (phase C). The slot is empty on the way in, and the
	/// turn empties it again on the way out so the next test to hold it finds it as the markup
	/// declares it.
	/// </summary>
	/// <param name="cancellationToken">The calling test's token.</param>
	/// <param name="exclusive">
	/// True to take the app to yourself as well as the slot, for an edit whose correctness depends on
	/// the rest of the tree holding still. No test needs it today. The removal test did, or was
	/// believed to: it passed alone and failed in company, and exclusivity was given to it as a fix
	/// pending an explanation. The explanation turned out to be the fixture emitting its own cleanup
	/// removals in document order so that each renumbered the next (D36), which exclusivity never
	/// addressed and only hid. Kept because the capability is real and the next test to want it should
	/// not have to rebuild it -- but a test reaching for this should say what it is protecting against,
	/// since last time the honest answer was "nothing, the bug is elsewhere".
	/// </param>
	public async Task<SlotTurn> TakeSlotAsync(CancellationToken cancellationToken, bool exclusive = false)
	{
		await EnterAsync(writer: exclusive, needsXamlProvider: true, cancellationToken);

		int slot;
		lock (_gate)
		{
			if (_freeSlots.Count == 0)
			{
				if (exclusive) _phases.ReleaseWriter();
				else _phases.ReleaseReader();
				throw new InvalidOperationException(
					$"Every one of the {SlotCount} scratch slots is in use. Add more <Grid x:Name=\"SlotN\" /> to "
						+ "the probe's Scratch panel and raise SlotCount; running out is a fact about how many "
						+ "tests overlap, not a failure of the test that happened to ask last.");
			}

			slot = _freeSlots.Pop();
		}

		return new SlotTurn(this, await SharedSessionAsync(cancellationToken), slot, exclusive);
	}

	/// <summary>Common entry: make sure everything is built, then take the phase gate.</summary>
	private async Task<string> EnterAsync(bool writer, bool needsXamlProvider, CancellationToken cancellationToken)
	{
		// Built before the gate is taken, and under its own lock, so a half-minute of MSBuild is not
		// done while holding a gate every other test is queued on.
		string aumid;
		await _oneAtATime.WaitAsync(cancellationToken);
		try
		{
			aumid = AumidCore(needsXamlProvider);
		}
		finally
		{
			_oneAtATime.Release();
		}

		if (writer) await _phases.EnterWriterAsync(cancellationToken);
		else await _phases.EnterReaderAsync(cancellationToken);

		return aumid;
	}

	/// <summary>
	/// The shared launched app, started on first use and kept for the run.
	/// <para>
	/// Re-launched rather than resurrected when it is gone, because a phase A test kills it: that is
	/// what phase A is for. Checking rather than assuming is what stops the first phase B test after
	/// a phase A one from failing for a reason that has nothing to do with it.
	/// </para>
	/// </summary>
	private async Task<LiveAppSession> SharedSessionAsync(CancellationToken cancellationToken)
	{
		if (Usable(_sharedSession)) return _sharedSession!;

		await _relaunch.WaitAsync(cancellationToken);
		try
		{
			// Asked again inside the lock. Everyone queued here arrived because the app was down, and
			// all but the first are now looking at the app the first one launched -- so without this
			// they would each tear down a healthy app to build the same one again, which is the
			// stampede the lock is here to stop rather than merely to serialise.
			if (Usable(_sharedSession)) return _sharedSession!;

			return await LaunchSharedAsync(cancellationToken);
		}
		finally
		{
			_relaunch.Release();
		}
	}

	/// <summary>
	/// Whether a session can be handed to a test. Ready is the session's opinion of itself and it
	/// outlives the app: a phase A test kills the process, and the session goes on reporting Ready
	/// until something asks it to do work. So the process question is asked of the operating system,
	/// which is the only party that knows.
	/// </summary>
	private static bool Usable(LiveAppSession? session) =>
		session is not null && session.Describe().State == LiveAppSessionState.Ready && AppIsRunning();

	private async Task<LiveAppSession> LaunchSharedAsync(CancellationToken cancellationToken)
	{
		await CloseSharedAsync();
		await StopAppAndWaitAsync();

		_sharedManager = new LiveAppSessionManager(
			Options.Create(new BrokerOptions()),
			NullLoggerFactory.Instance,
			NullLogger<LiveAppSessionManager>.Instance);

		// Retried, because launching a packaged app under a debugger is not reliable on the first try
		// when the previous instance has only just gone: the resume stub fails to connect and the
		// session comes up Faulted with a message about the app not activating, which describes the
		// symptom rather than the race. Three attempts, each after the app is confirmed gone.
		LiveAppSession? session = null;
		LiveAppSessionSummary? summary = null;
		for (var attempt = 1; attempt <= 3; attempt++)
		{
			session = await _sharedManager.StartAsync(
				new LiveAppTarget
				{
					Kind = LiveAppTargetKind.LaunchUwp,
					AppUserModelId = _aumid!,
					Description = "shared uwp probe",
				},
				cancellationToken);

			summary = session.Describe();
			if (summary.State == LiveAppSessionState.Ready) break;

			await _sharedManager.CloseAsync(session.SessionId, cancellationToken);
			await StopAppAndWaitAsync();
			await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
			session = null;
		}

		if (session is null)
		{
			throw new InvalidOperationException(
				$"The shared probe session would not come up Ready after three attempts: {summary?.Detail}");
		}

		// The app is Ready as soon as the debugger has it, which is before the first frame exists. Its
		// timer exception is the signal that the tree is up, and every XAML read wants that to have
		// happened -- waiting once here is what keeps it out of every test.
		await WaitForFirstTickAsync(session, cancellationToken);

		_sharedSession = session;
		return session;
	}

	/// <summary>Whether an instance of the probe app is running right now.</summary>
	private static bool AppIsRunning()
	{
		var running = Process.GetProcessesByName(ProcessName);
		foreach (var process in running) process.Dispose();

		return running.Length > 0;
	}

	/// <summary>
	/// Ends the app and waits for it to actually be gone.
	/// <para>
	/// Killing a process and the package being launchable again are not the same moment. Activating
	/// while the previous instance is still terminating fails, and it fails as a Faulted session with
	/// nothing in it that says "you were too quick" -- which is exactly how it presented: a phase B
	/// test blaming the app for coming up Faulted, immediately after a phase A test had ended it.
	/// </para>
	/// </summary>
	private async Task StopAppAndWaitAsync()
	{
		StopApp();

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
		while (DateTime.UtcNow < deadline)
		{
			if (!AppIsRunning()) return;
			await Task.Delay(100);
		}
	}
	private static async Task WaitForFirstTickAsync(LiveAppSession session, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		while (DateTime.UtcNow < deadline)
		{
			var events = await session.ReadEventsAsync(0, cancellationToken);
			if (events.Events.Any(entry => entry.ExceptionType?.Contains("RoseUwpProbeException") ?? false)) return;

			await Task.Delay(150, cancellationToken);
		}

		throw new InvalidOperationException("The shared probe app never reported its first tick, so its tree is not up.");
	}

	private async Task CloseSharedAsync()
	{
		var manager = _sharedManager;
		_sharedManager = null;
		_sharedSession = null;

		if (manager is not null) await manager.DisposeAsync();
	}

	/// <summary>Phase A: the app, launched by the test itself.</summary>
	public sealed class AppTurn(UwpProbeApp probe, string aumid) : IAsyncDisposable
	{
		public string Aumid { get; } = aumid;

		public ValueTask DisposeAsync()
		{
			// The app this test started goes with it, so the next shared session starts a fresh one
			// rather than activating this.
			probe.StopApp();
			probe._phases.ReleaseWriter();
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Phase B: the shared app, to this test alone, handed back as it was found.</summary>
	public sealed class SessionTurn(UwpProbeApp probe, LiveAppSession session) : IAsyncDisposable
	{
		public LiveAppSession Session { get; } = session;

		/// <summary>
		/// Checks the app was left in the state it was found in, and fails if it was not.
		/// <para>
		/// This is the price of a shared app, and it is deliberately paid by the test that broke the
		/// rule rather than by whichever test runs next. A selection left behind, or select mode left
		/// armed, is invisible to the test that leaves it and turns the following test's assertion
		/// into a puzzle about a fixture -- the exact failure that makes people stop trusting a suite.
		/// So it is checked at the moment it can still be attributed.
		/// </para>
		/// </summary>
		public async ValueTask DisposeAsync()
		{
			try
			{
				// Bounded, because this runs after the test's own assertions and a check that can hang
				// turns a failing test into a hanging suite -- which is what it did: fifteen minutes
				// with no output, against a run that takes three.
				using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));
				var left = await Session.ReadXamlSelectionAsync(bounded.Token);

				// Best effort at putting it right, so one offending test does not cascade. It still
				// fails below: cleaning up after it is not the same as it having been clean.
				if (left.Selected || left.Armed) await Session.ClearXamlSelectionAsync(bounded.Token);

				Assert.False(
					left.Selected,
					$"this test left {left.Name ?? left.Address ?? "an element"} selected. A phase B test holds the "
						+ "whole app, so it has to hand it back unselected.");

				Assert.False(left.Armed, "this test left select mode armed. Disarm it before the test ends.");
			}
			finally
			{
				probe._phases.ReleaseWriter();
			}
		}
	}

	/// <summary>Phase C: the shared app, plus one empty slot this test owns.</summary>
	public sealed class SlotTurn(UwpProbeApp probe, LiveAppSession session, int slot, bool exclusive) : IAsyncDisposable
	{
		public LiveAppSession Session { get; } = session;

		/// <summary>The slot's <c>x:Name</c>, which is also the anchor every address in it hangs off.</summary>
		public string Slot { get; } = $"Slot{slot}";

		// A diff is given markup, not a document, and it parses what it is given as XML. A fragment
		// lifted out of MainPage.xaml has none of the document's namespace declarations, so x:Name is
		// an undeclared prefix and the whole apply is refused before it starts. The declarations go on
		// the fragment root, where they cost nothing and cannot be forgotten by a caller.
		internal const string NamespacesFor =
			" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
				+ " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

		/// <summary>The empty slot as the probe's markup declares it, for the "before" side of a diff.</summary>
		public string EmptyMarkup => $"<Grid x:Name=\"{Slot}\"{NamespacesFor} />";

		/// <summary>The slot holding <paramref name="children"/>, for the "after" side of a diff.</summary>
		public string MarkupHolding(string children) => $"<Grid x:Name=\"{Slot}\"{NamespacesFor}>{children}</Grid>";

		/// <summary>An address inside this slot, anchored on its name so nothing outside can move it.</summary>
		public string Address(string relative) => $"#{Slot}/{relative}";

		public async ValueTask DisposeAsync()
		{
			try
			{
				// Emptied by the same route a test fills it, so a slot handed on is the slot the
				// markup declares rather than whatever the last test happened to leave.
				await probe.EmptySlotAsync(Session, Slot);
			}
			finally
			{
				lock (probe._gate)
				{
					probe._freeSlots.Push(slot);
				}

				if (exclusive) probe._phases.ReleaseWriter();
				else probe._phases.ReleaseReader();
			}
		}
	}

	/// <summary>
	/// Removes everything a test put in its slot, by reading what is actually there rather than by
	/// replaying what the test said it added. A test that failed half way through added some of its
	/// elements and not others, and that is exactly when the slot most needs emptying.
	/// <para>
	/// It then checks it worked, which is the same rule phase B turns already keep and for the same
	/// reason: a slot handed on still occupied is not a tidiness problem, it is a wrong answer given
	/// confidently to whichever test is handed it next. That test finds elements it did not add,
	/// counts them, and fails somewhere else entirely -- and because slots come off a stack, the slot
	/// that comes back is almost always the one that goes straight back out. This is how the
	/// last-first ordering in <c>XamlDiff</c> was found: emptying a slot holding two elements removed
	/// one, failed on the other, and said nothing, because nothing here looked at the result.
	/// </para>
	/// </summary>
	private async Task EmptySlotAsync(LiveAppSession session, string slot)
	{
		var subtree = await session.ReadXamlTreeAsync(slot, offset: 0, limit: 0, CancellationToken.None);
		var anchor = subtree.Nodes.FirstOrDefault(node => node.Name == slot);
		if (anchor is null) return;

		var children = subtree.Nodes.Where(node => node.Parent == anchor.Handle).ToList();
		if (children.Count == 0) return;

		var held = string.Concat(children.Select(child => $"<{Local(child.TypeName)} />"));
		var applied = await session.ApplyXamlAsync(
			$"<Grid x:Name=\"{slot}\"{SlotTurn.NamespacesFor}>{held}</Grid>",
			$"<Grid x:Name=\"{slot}\"{SlotTurn.NamespacesFor} />",
			filePath: null,
			CancellationToken.None);

		// Checked off what the apply reports rather than by reading the slot back, which is the same
		// answer for nothing: an edit that did not land says so here, and a confirming read would be
		// another injection into an app the whole suite is already queueing behind.
		var refused = applied.Results.Where(result => result.Status != "applied").ToList();
		if (applied.Detail is null && refused.Count == 0) return;

		var statuses = string.Join(", ", refused.Select(result => $"{result.Kind} {result.Target} => {result.Status}"));
		throw new InvalidOperationException(
			$"{slot} could not be emptied, so the next test handed it would have found {children.Count} "
				+ $"element(s) it did not add and failed for a reason of its own. Detail: {applied.Detail ?? "(none)"}. "
				+ $"Refused: {(statuses.Length == 0 ? "(none)" : statuses)}");
	}

	/// <summary>The local half of a CLR type name, which is what markup and the diff both count by.</summary>
	private static string Local(string? typeName)
	{
		if (string.IsNullOrEmpty(typeName)) return "Border";

		var dot = typeName.LastIndexOf('.');
		return dot >= 0 && dot < typeName.Length - 1 ? typeName[(dot + 1)..] : typeName;
	}

	/// <summary>
	/// Lets phase A and phase B tests have the app to themselves while phase C tests may overlap each
	/// other. A reader/writer gate rather than one lock, written out here because .NET has no
	/// asynchronous one and a synchronous lock held across a test would block xUnit's threads.
	/// </summary>
	private sealed class PhaseGate
	{
		private readonly SemaphoreSlim _turnstile = new(1, 1);
		private readonly SemaphoreSlim _noReaders = new(1, 1);
		private readonly Lock _count = new();
		private int _readers;

		public async Task EnterWriterAsync(CancellationToken cancellationToken)
		{
			// The turnstile first, which also stops new readers arriving, then wait for the readers
			// already inside to leave.
			await _turnstile.WaitAsync(cancellationToken);
			try
			{
				await _noReaders.WaitAsync(cancellationToken);
			}
			catch
			{
				_turnstile.Release();
				throw;
			}
		}

		public void ReleaseWriter()
		{
			_noReaders.Release();
			_turnstile.Release();
		}

		public async Task EnterReaderAsync(CancellationToken cancellationToken)
		{
			await _turnstile.WaitAsync(cancellationToken);
			try
			{
				var first = false;
				lock (_count)
				{
					first = ++_readers == 1;
				}

				// Only the first reader claims the no-readers token; the rest are already covered by
				// it, which is what lets them run together.
				if (first) await _noReaders.WaitAsync(cancellationToken);
			}
			finally
			{
				_turnstile.Release();
			}
		}

		public void ReleaseReader()
		{
			lock (_count)
			{
				if (--_readers > 0) return;
			}

			_noReaders.Release();
		}
	}
	/// <summary>
	/// Ends the running app, so the next test's launch starts a fresh process rather than activating
	/// this one.
	/// <para>
	/// This is the half of the old per-test <c>Remove-AppxPackage</c> that was load-bearing and is easy
	/// to lose sight of: unregistering also terminated the app. A packaged app is single-instance, so
	/// launching by AUMID with the previous instance still up activates that one instead of starting
	/// one under the debugger -- and a from-birth attach then has nothing to attach to.
	/// </para>
	/// </summary>
	public void StopApp()
	{
		foreach (var process in Process.GetProcessesByName(ProcessName))
		{
			try
			{
				if (!process.HasExited) process.Kill(entireProcessTree: true);
				process.WaitForExit(5000);
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// It exited between the enumeration and the kill, or it is already going. Either way
				// the postcondition holds.
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	/// <summary>
	/// Unregisters the package, once, after every test in the assembly. Nothing to do where no test
	/// ever asked for it, which is every run on a machine without the UWP tooling.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		// The shared app goes first, because unregistering a package with a debugger attached to it is
		// the untidy version of the same thing.
		await CloseSharedAsync();
		_relaunch.Dispose();

		lock (_gate)
		{
			if (!_registered) return;

			StopApp();
			RunProcess(
				"powershell",
				$"-NoProfile -NonInteractive -Command \"Get-AppxPackage '{PackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"");
			_registered = false;
		}
	}

	/// <summary>
	/// The MSBuild that can build classic UWP, found via vswhere, or null when no such Visual Studio is
	/// installed. Probed once, including the null: twenty vswhere processes to reach the same answer is
	/// the cheapest of the things this class stopped repeating, and still not free.
	/// </summary>
	private string? MsBuild()
	{
		if (_msBuildProbed) return _msBuild;
		_msBuildProbed = true;

		var vswhere = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			"Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vswhere)) return null;

		var (exitCode, output) = RunProcess(
			vswhere,
			"-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe");
		if (exitCode != 0) return null;

		var msbuild = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase));
		if (msbuild is null || !File.Exists(msbuild)) return null;

		// MSBuild alone is not enough; the classic-UWP C# targets must be installed too.
		var windowsXaml = Path.Combine(Path.GetDirectoryName(msbuild)!, "..", "..", "..", "MSBuild", "Microsoft", "WindowsXaml");
		return _msBuild = Directory.Exists(Path.GetFullPath(windowsXaml)) ? msbuild : null;
	}

	/// <summary>
	/// Builds the native XAML diagnostics provider (x64) with build.ps1. Returns false only when the
	/// toolchain is genuinely absent, so the caller skips; anything else throws.
	/// <para>
	/// That distinction is the point. This used to return false for any non-zero exit and the caller
	/// skipped with the message "no C++ toolset", which meant a compile error in the provider -- or
	/// two builds racing over one PDB, which is how it was noticed -- silently skipped the XAML tests
	/// and left the suite green. A capability quietly not being tested is worse than a red build, and
	/// looks identical to a machine that simply cannot build it. build.ps1 already separates the two:
	/// it exits 3 from its own Fail for a missing toolset or SDK, and anything else is a real failure.
	/// </para>
	/// <para>
	/// Building it once also removes that PDB race by construction rather than by luck.
	/// </para>
	/// </summary>
	private bool ProviderBuilt()
	{
		if (_providerProbed) return _providerBuilt;
		_providerProbed = true;

		var script = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "build.ps1");
		if (!File.Exists(script)) return false;

		var (exitCode, output) = RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Platform x64 -Configuration Debug");

		// 3 is build.ps1's Fail: no MSVC toolset, or no Windows SDK. The only skippable outcome.
		if (exitCode == 3) return false;

		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"Building the XAML provider failed (exit {exitCode}):{Environment.NewLine}{output}");
		}

		var dll = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "bin", "x64", "Debug", "RoseMcp.Xaml.Uwp.Tap.dll");
		if (!File.Exists(dll))
		{
			throw new InvalidOperationException($"The XAML provider build reported success but produced no {dll}.");
		}

		return _providerBuilt = true;
	}

	private static string AppDirectory() => Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic");

	/// <summary>Builds the classic UWP probe app Debug|x64 and returns its build output directory.</summary>
	private static string Build(string msbuild)
	{
		var csproj = Path.Combine(AppDirectory(), "Rose.ProbeApp.UwpClassic.csproj");

		var restore = RunProcess(msbuild, $"\"{csproj}\" -t:Restore -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (restore.ExitCode != 0) throw new InvalidOperationException($"UWP restore failed:{Environment.NewLine}{restore.Output}");

		var build = RunProcess(msbuild, $"\"{csproj}\" -t:Build -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (build.ExitCode != 0) throw new InvalidOperationException($"UWP build failed:{Environment.NewLine}{build.Output}");

		return Path.Combine(AppDirectory(), "bin", "x64", "Debug");
	}

	/// <summary>Stages the deployable AppX layout the way Visual Studio's deploy stages it.</summary>
	private static string Stage(string buildOutputDirectory)
	{
		var recipePath = Path.Combine(buildOutputDirectory, "Rose.ProbeApp.UwpClassic.build.appxrecipe");
		if (!File.Exists(recipePath)) throw new InvalidOperationException($"No appxrecipe at {recipePath}; the UWP build did not complete.");

		XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
		var recipe = XDocument.Load(recipePath);

		var layoutText = recipe.Descendants(ns + "LayoutDir").FirstOrDefault()?.Value
			?? throw new InvalidOperationException("The appxrecipe declares no LayoutDir.");
		var layoutDirectory = Uri.UnescapeDataString(layoutText);

		if (Directory.Exists(layoutDirectory)) Directory.Delete(layoutDirectory, recursive: true);
		Directory.CreateDirectory(layoutDirectory);

		// Both the manifest and every packaged file carry an Include (the source on disk, MSBuild-escaped)
		// and a PackagePath (where it lands in the layout).
		var entries = recipe.Descendants(ns + "AppXManifest").Concat(recipe.Descendants(ns + "AppxPackagedFile"));
		foreach (var entry in entries)
		{
			var source = Uri.UnescapeDataString(entry.Attribute("Include")!.Value);
			var packagePath = Uri.UnescapeDataString(entry.Element(ns + "PackagePath")!.Value);
			var destination = Path.Combine(layoutDirectory, packagePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(source, destination, overwrite: true);
		}

		return layoutDirectory;
	}

	/// <summary>
	/// Registers the loose UWP layout and returns its AUMID, or null when registration is not permitted
	/// (developer mode off), so the test can skip rather than fail on an environment limit.
	/// </summary>
	private static string? Register(string layoutDirectory)
	{
		var manifest = Path.Combine(layoutDirectory, "AppxManifest.xml");
		var script =
			$"try {{ Add-AppxPackage -Register '{manifest}' -ErrorAction Stop }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 0 }}; "
				+ $"$p = Get-AppxPackage '{PackageName}'; if ($p) {{ Write-Output ('PFN: ' + $p.PackageFamilyName) }}";
		var (_, output) = RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");

		var pfnLine = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));
		if (pfnLine is null) return null;

		return $"{pfnLine["PFN: ".Length..].Trim()}!App";
	}
}
