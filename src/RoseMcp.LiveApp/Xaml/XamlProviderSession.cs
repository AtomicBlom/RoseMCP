using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Gets a XAML diagnostics provider resident in the target and hands back the channel to it.
/// <c>InitializeXamlDiagnosticsEx</c> loads the provider into the app by pid, out of a working folder
/// this side stages; the two ends then talk over a named pipe the provider connects back on. The
/// provider must match the target's architecture, which is this host's architecture -- an x64 provider
/// for a classic UWP app emulated on ARM64.
/// <para>
/// Which provider, which library exports the initialiser, which class id, and whether that folder
/// needs AppContainer grants are all asked of the target rather than assumed. Four separate
/// hard-codings of UWP cost not that WinUI 3 failed -- it is that it failed after a twenty-second
/// wait, blaming the app for not being packaged. <see cref="XamlStackProbe"/> reads the framework
/// DLLs the process has loaded and <see cref="XamlTaps"/> maps the answer to a tap, so a stack with
/// no provider is refused immediately and by name.
/// </para>
/// <para>
/// Separate from <see cref="XamlDiagnosticsSession"/>, which reads what the provider reports, because
/// the two are governed by different rules: staging a DLL, granting ALL APPLICATION PACKAGES and
/// matching an architecture answer to <c>hosts-and-deploy.md</c>, while asking the provider a question
/// answers to <c>xaml-live-edit.md</c>. Nothing here knows a single word of the wire format.
/// </para>
/// <para>
/// Nothing here takes a lock, and every method assumes the caller holds
/// <see cref="XamlDiagnosticsSession"/>'s. Injection has to be serialised with the requests it makes
/// possible -- a second injection while a request is in flight loads a second tap into the app, and
/// deleting the work folder under one pulls the provider out from beneath it -- and one lock held by
/// the object that fields the calls is what makes that true without either half locking twice.
/// </para>
/// </summary>
internal sealed class XamlProviderSession(ILogger logger) : IDisposable
{
	// HRESULT_FROM_WIN32(ERROR_NOT_FOUND): the well-known diagnostics endpoint is not there yet.
	private const int ErrorNotFound = unchecked((int)0x80070490);

	/// <summary>
	/// Every bound on a wait for the XAML provider, and the sentence each one produces when it expires.
	/// Long enough for a XAML app to get its first tree up, short enough that a target which genuinely
	/// has no XAML UI does not hold a tool call for an uncomfortable length of time -- and bounded
	/// without exception, because a wait with no bound here is a tool call that never returns rather
	/// than a slow one.
	/// <para>
	/// Read here and shared with the caller rather than read twice, because the ceiling that shortens
	/// them is one environment variable and a test setting it expects to have shortened every wait a
	/// request can hit, not four of the five.
	/// </para>
	/// </summary>
	internal XamlChannelBounds Bounds { get; } = XamlChannelBounds.FromEnvironment();

	private string? _workDir;
	private string? _stagedProvider;

	// Which XAML framework the target turned out to be, and the tap serving it. Resolved once and
	// kept: a session has exactly one target process, so the stack cannot change underneath it, and
	// re-reading the module list on every request would pay for an answer that cannot have moved.
	private XamlStackDetection? _stack;

	private XamlTap? _tap;

	private InitializeXamlDiagnosticsEx? _initialise;

	// The XAML framework dll the initialiser is pointed at, resolved from the target, or null where
	// the framework exports its own initialiser and needs no telling.
	private string? _diagnosticsPath;

	// The host end of the pipe the provider connects back on, and the only way a request reaches it.
	// A pipe an AppContainer cannot reach is a session with no XAML in it, not a slower one.
	private XamlProviderPipe? _pipe;

	// How many times this session has loaded the provider. One is the intent and the ordinary case;
	// anything more means the pipe dropped and the channel was rebuilt.
	private int _injections;

	// Whether the target's diagnostics endpoint has ever answered this session. It separates two
	// failures that share an HRESULT and mean opposite things: ERROR_NOT_FOUND before any read is an
	// app whose tree is not up yet, or one with no XAML at all, and waiting is the advice. The same
	// code after a read has succeeded is an app that was serving us and has stopped, where waiting is
	// exactly the wrong advice -- it was reported costing an hour of looking at the wrong app, because
	// the message offered "still starting" and "no XAML UI" and neither had been true for some time.
	private bool _endpointAnswered;

	/// <summary>Which XAML framework the target turned out to be, or null until a tap has been resolved.</summary>
	internal XamlStackDetection? Stack => _stack;

	/// <summary>
	/// Whether a provider is resident, which is what decides the cost of the next request: a message
	/// to a reader already in the app, or an injection first. Three states rather than a bool, because
	/// a pipe that has dropped is not the same as one that was never opened.
	/// </summary>
	internal LiveXamlProvider Residency
	{
		get
		{
			if (_pipe?.Connected == true) return LiveXamlProvider.Resident;

			return _injections > 0 ? LiveXamlProvider.Lost : LiveXamlProvider.None;
		}
	}

	/// <summary>
	/// The pipe a resident provider is already answering on, or null where nothing is resident.
	/// Distinct from <see cref="Connect"/> by what it refuses to do: a caller reading this asks what is
	/// there and never injects, which is how the in-app toolbar's own selection is read by a caller
	/// that must not load anything to find out whether a person has picked something.
	/// </summary>
	internal XamlProviderPipe? ResidentPipe => _pipe?.Connected == true ? _pipe : null;

	/// <summary>
	/// Makes sure the provider is loaded and answering on its pipe, injecting once if it is not. Returns
	/// the pipe every request rides, or the sentence saying why there is not one.
	/// </summary>
	/// <remarks>
	/// Injection loads the provider and does nothing else. Every request is a message, because the work
	/// has to happen on the app's UI thread and injection was only ever the way onto that thread before
	/// there was a resident reader that could reach it through the dispatcher.
	/// <para>
	/// Once per session rather than once per call, which is the whole of what made a session accumulate
	/// advised taps -- each one receiving every mutation in the app, holding a copy of its tree, and
	/// costing the UI thread the next injection needs.
	/// </para>
	/// <para>
	/// The pipe comes back with the answer rather than being left for the caller to fetch, because "it
	/// is ready" and "here is the channel" are one fact. Split in two, a caller holds a reason that was
	/// true and a pipe that has dropped since, and the null-forgiveness it needs to use the second is
	/// exactly the claim it cannot make.
	/// </para>
	/// </remarks>
	internal (XamlProviderPipe? Pipe, string? Unready) Connect(int pid)
	{
		if (_pipe?.Connected == true) return (_pipe, null);

		// Said, because one injection per session is the invariant and this is the only thing that can
		// break it. A pipe that drops sends the next call back through injection, which loads a second
		// tap into the app -- the condition that used to accumulate one per request. The previous tap
		// stands itself down, so the cost is bounded, but a session doing this repeatedly is a channel
		// failing quietly and it should not take a memory graph to notice.
		if (_injections > 0)
		{
			logger.LogWarning(
				"Injecting into pid {Pid} again (injection {Count}) because the XAML provider's pipe is not connected. "
					+ "One injection per session is the intent; more than one means the channel dropped.",
				pid,
				_injections + 1);
		}

		var (_, error) = Inject(pid);
		if (error is not null) return (null, error);

		_injections++;

		if (_pipe?.Connected != true)
		{
			return (null, "The XAML provider loaded but did not connect back on its pipe, which is how every request "
				+ $"reaches it. It was given {Bounds.Greeting.TotalSeconds:0.##}s to connect.");
		}

		return (_pipe, null);
	}

	/// <summary>
	/// Stages the provider and injects it. Returns the working folder, or an error string when the
	/// provider is unavailable, staging fails, or injection is rejected.
	/// </summary>
	private (string? WorkDir, string? Error) Inject(int pid)
	{
		var (tap, tapError) = ResolveTap(pid);
		if (tap is null) return (null, tapError);

		var provider = ResolveProviderPath(tap);
		if (provider is null)
		{
			return (null, $"The XAML provider ({tap.ProviderFileName}) was not found for this host's architecture; build src/{tap.ProviderProjectName} for {ProviderPlatform()}.");
		}

		string workDir;
		string stagedProvider;
		try
		{
			(workDir, stagedProvider) = StageSandboxFolder(tap, provider);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Staging the XAML provider sandbox folder failed.");
			return (null, $"Could not stage the XAML provider: {exception.Message}");
		}

		// Retried, because the common failure here is transient and a one-shot message called it fatal.
		// The XAML diagnostics endpoint does not exist until the framework has built a tree, so a
		// session that has only just attached -- which is exactly when an agent asks -- gets
		// ERROR_NOT_FOUND for a second or two. A caller told "the target may have no XAML UI" about a
		// XAML app concludes the tool does not work on their app, and stops. It was reported that way
		// from a real session: the same call twelve seconds later returned 629 nodes.
		// Timed, because how long the endpoint took to answer is the one number that separates a session
		// that goes on working from one that wedges, and it was only ever recoverable by subtracting two
		// log timestamps by hand. InitializeXamlDiagnosticsEx does not return until the target's side has
		// created and sited the tap, so this measures the target's UI thread as much as our own work: a
		// handshake of seconds means that thread was saturated while we injected into it.
		var deadline = DateTime.UtcNow + Bounds.Endpoint;
		var handshake = Stopwatch.StartNew();
		var hr = 0;
		while (true)
		{
			// wszInitializationData is an arbitrary string handed to the TAP, and it already carries
			// the work directory, so the pipe name rides in the same slot -- no new plumbing to
			// establish the channel. Separated by '|', which cannot occur in a Windows path.
			var initData = _pipe is null ? workDir : $"{workDir}|{_pipe.Name}";

			var attempt = Initialise(tap, pid, stagedProvider, initData);
			if (attempt is null) return (null, WedgedInjectionDetail(pid));

			hr = attempt.Value;
			if (hr >= 0)
			{
				NoteProviderPipe();

				logger.LogInformation(
					"The target's XAML diagnostics endpoint answered in {HandshakeMs}ms on pid {Pid}.",
					handshake.ElapsedMilliseconds,
					pid);

				_endpointAnswered = true;
				return (workDir, null);
			}
			if (hr != ErrorNotFound || DateTime.UtcNow >= deadline) break;

			Thread.Sleep(250);
		}

		// Two failures, said apart. ERROR_NOT_FOUND after waiting is the endpoint never appearing,
		// which is what "no XAML UI" actually looks like; anything else is its own HRESULT and should
		// not be explained away as a missing UI.
		//
		// It no longer offers "or is not a packaged app". That was wrong twice over: packaging has
		// nothing to do with whether the endpoint appears, and unpackaged WinUI 3 is an ordinary
		// supported shape. The stack is known by the time this runs, so the message can name it
		// rather than guess at causes.
		// A third case, and it is the one that reads worst when it is folded into the second: the endpoint
		// answered earlier in this very session and has stopped. Neither "still starting" nor "no XAML UI"
		// can be true of an app that has already handed us a tree, so saying either sends the caller to
		// look at their own app. What it actually indicates is the target's UI thread no longer serving,
		// and the handshake time is quoted because a slow one is the warning that precedes this.
		var detail = hr != ErrorNotFound
			? $"InitializeXamlDiagnosticsEx failed (0x{hr:x8})."
			: _endpointAnswered
				? XamlChannelBounds.TimedOut("the target's XAML diagnostics endpoint", Bounds.Endpoint)
					+ $" It answered earlier in this session and has stopped (0x{ErrorNotFound:x8}), so the target is "
					+ "not starting up and does have a XAML UI. Its UI thread is no longer serving diagnostics: check "
					+ "whether the process is spinning a core, and if it is, the app will not recover and has to be "
					+ "restarted. Reads before this one took "
					+ $"{handshake.ElapsedMilliseconds}ms to be answered."
				: XamlChannelBounds.TimedOut("the target's XAML diagnostics endpoint", Bounds.Endpoint)
					+ $" It never appeared (0x{ErrorNotFound:x8}). The target was detected as {_stack!.Stack} because "
					+ $"{_stack.Reason}. A XAML app that is still starting can take a moment; if it persists, the "
					+ "target has no XAML UI.";

		logger.LogWarning(
			"The target's XAML diagnostics endpoint did not answer within {HandshakeMs}ms on pid {Pid} "
				+ "(0x{Hr:x8}); it had answered before in this session: {Answered}.",
			handshake.ElapsedMilliseconds,
			pid,
			hr,
			_endpointAnswered);

		return (null, detail);
	}

	/// <summary>
	/// One <c>InitializeXamlDiagnosticsEx</c> call, bounded. Returns the HRESULT, or null when the
	/// call did not come back inside <see cref="XamlChannelBounds.Injection"/>.
	/// <para>
	/// It is a blocking cross-process call that does not return until the target's side has created
	/// and sited the tap, and on WinUI 3 that means the app's UI thread has run the tap's body. A
	/// target whose UI thread is stuck below managed code therefore never returns from it -- which is
	/// how a suite run came to hang for fifty minutes on a first tree read, with the pipe logged as
	/// listening and no line after it.
	/// </para>
	/// <para>
	/// Bounded by running it on a thread of its own and abandoning that thread, because there is no
	/// other way to bound a blocking P/Invoke: the call cannot be cancelled and the native side holds
	/// no token. The thread is a background thread, so an abandoned injection cannot keep this process
	/// from exiting -- which matters more than reclaiming it, since the host has to be able to die
	/// with its client whatever the target is doing.
	/// </para>
	/// </summary>
	private int? Initialise(XamlTap tap, int pid, string stagedProvider, string initData)
	{
		var result = 0;
		var thread = new Thread(() => result = _initialise!(
			tap.EndpointName, (uint)pid, _diagnosticsPath, stagedProvider, tap.ProviderClsid, initData))
		{
			IsBackground = true,
			Name = "rose-xaml-inject",
		};

		thread.Start();
		if (thread.Join(Bounds.Injection)) return result;

		logger.LogWarning(
			"InitializeXamlDiagnosticsEx into pid {Pid} did not return within {Seconds}s; abandoning it.",
			pid,
			Bounds.Injection.TotalSeconds);

		return null;
	}

	/// <summary>
	/// What to tell a caller whose injection never came back. It names the channel, the bound, and the
	/// one thing that explains it, because from outside this is indistinguishable from every other
	/// way a tree read comes back empty.
	/// </summary>
	private string WedgedInjectionDetail(int pid) =>
		XamlChannelBounds.TimedOut("the XAML diagnostics injection call", Bounds.Injection)
			+ $" InitializeXamlDiagnosticsEx into pid {pid} did not return. It is served by the target's UI "
			+ "thread, so an app that is wedged, or stopped at a breakpoint, never lets it finish. The "
			+ "provider may still load if the app frees that thread.";

	/// <summary>
	/// Waits for the provider to connect back on the pipe, which every request rides. One that never
	/// connects is logged here and refused by <see cref="Connect"/>, so the bound it was given
	/// is the only thing that explains a session where nothing can reach the tap.
	/// </summary>
	private void NoteProviderPipe()
	{
		if (_pipe is null || _pipe.Connected) return;

		// The pipe says what it read as the greeting; what is worth adding is how long it was given,
		// because a provider loaded into a saturated UI thread and one that never loaded at all are
		// the same silence until the bound is in the line.
		if (_pipe.WaitForProvider(Bounds.Greeting) is null)
		{
			logger.LogWarning(
				"The XAML provider did not connect on {PipeName} within {Seconds}s; no request can reach it.",
				_pipe.Name,
				Bounds.Greeting.TotalSeconds);
		}
	}
	private (string WorkDir, string StagedProvider) StageSandboxFolder(XamlTap tap, string provider)
	{
		// Stage once per session and reuse: the first injection loads the provider DLL into the target,
		// which holds the file open, so a later injection cannot overwrite it -- and need not, since it
		// is the same provider. Each request re-injects from this one staged copy.
		if (_workDir is not null && _stagedProvider is not null && File.Exists(_stagedProvider))
		{
			return (_workDir, _stagedProvider);
		}

		var root = Path.Combine(Path.GetTempPath(), "RoseMcpXaml");

		// Before staging anything, clear out what earlier hosts left behind. Nothing ever deleted
		// these: 146 folders and 225.6 MB of them on the machine this was found on, each holding a
		// copy of the provider and each carrying a grant to ALL APPLICATION PACKAGES, so they are
		// world-readable directories accumulating in the user's TEMP.
		SweepDeadSandboxFolders(root);

		var workDir = Path.Combine(root, Environment.ProcessId.ToString());

		// Our own pid's folder goes too, because a pid is reusable. A host that draws a recycled pid
		// used to find a populated folder and, worse than a stale state file, load a stale *provider*:
		// the copy below was skipped whenever the DLL was already there, so deploying a new provider
		// and getting the old one was silent and every symptom pointed at the change just made. It is
		// also why the fast rebuild loop (build the provider, copy it over, restart the app) worked at
		// all -- a new pid meant a fresh copy -- and it would have stopped working the first time a
		// pid came round again.
		TryDeleteDirectory(workDir);
		Directory.CreateDirectory(workDir);

		// Unconditional. The overwrite was always there and always unreachable behind the existence
		// test; it can only be reached now because the folder above is cleared first, which is why
		// the two halves of this fix have to land together. One file copy per session is nothing
		// beside injecting into a process.
		var stagedProvider = Path.Combine(workDir, tap.ProviderFileName);
		File.Copy(provider, stagedProvider, overwrite: true);

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		//
		// Asked of the tap rather than done always (#74). An unpackaged WinUI 3 app is not in an
		// AppContainer and needs none of it, and granting anyway would leave a world-readable
		// directory in TEMP for every session, for nothing.
		if (tap.NeedsAppContainerGrants)
		{
			foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
			{
				Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)");
			}
		}

		// Created with the folder rather than lazily, because the name has to exist before the first
		// injection carries it.
		_pipe = new XamlProviderPipe(logger);
		_pipe.Listen();

		_workDir = workDir;
		_stagedProvider = stagedProvider;
		return (workDir, stagedProvider);
	}

	/// <summary>
	/// Deletes the sandbox folders belonging to hosts that are gone, the way <c>RoseMcp.Logging</c>
	/// prunes its own sessions at startup.
	/// <para>
	/// A folder is named after the pid that made it, so "is that pid still running" is the whole test.
	/// A pid that has been recycled by some unrelated process reads as alive and its folder is kept,
	/// which is the safe direction to be wrong in: the cost is one abandoned folder until the next
	/// sweep, where deleting a live host's folder would pull the provider out from under it.
	/// </para>
	/// </summary>
	private void SweepDeadSandboxFolders(string root)
	{
		try
		{
			if (!Directory.Exists(root)) return;

			foreach (var folder in Directory.EnumerateDirectories(root))
			{
				if (!int.TryParse(Path.GetFileName(folder), out var pid)) continue;
				if (pid == Environment.ProcessId) continue; // Ours; the caller deals with it deliberately.
				if (IsAlive(pid)) continue;

				TryDeleteDirectory(folder);
			}
		}
		catch (Exception exception)
		{
			// Tidying, never the job: a folder that cannot be enumerated or removed costs disk and
			// nothing else, and failing a XAML call over it would be the wrong trade entirely.
			logger.LogDebug(exception, "Sweeping stale XAML provider sandbox folders under {Root} failed.", root);
		}
	}

	private static bool IsAlive(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false; // No process with that id.
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception)
		{
			// Whatever is still held belongs to an app that has the provider loaded, and that app
			// outlives the debug session on purpose -- detaching leaves it running. The next host to
			// start sweeps it once this pid is gone, which is why the sweep and this go together.
		}
	}

	/// <summary>
	/// Asks the resident provider to give back the two framework interfaces it holds, and says what
	/// it answered.
	/// <para>
	/// Over the pipe and acknowledged, rather than left to the provider noticing the pipe close. Both
	/// paths exist, because a host that is killed asks nothing -- but only the acknowledged one can be
	/// reported, and a release nobody can observe is one nobody can tell from the leak it replaces.
	/// </para>
	/// <para>
	/// Idempotent, so a caller that releases on detach and again on disposal is fine -- the provider
	/// answers the second with "already released".
	/// </para>
	/// <para>
	/// A session with no pipe cannot ask, and there is nothing else to ask through: a request reaches
	/// the provider on the pipe and nowhere else, and injecting again to say "stop" would load a second
	/// tap to release the first one's interfaces. That case is said rather than fixed.
	/// </para>
	/// </summary>
	internal void Release()
	{
		if (_pipe?.Connected != true)
		{
			logger.LogDebug(
				"No provider pipe to detach on; anything the provider still holds goes when the app does.");

			return;
		}

		var answered = _pipe.Request("detach", Bounds.Greeting);
		if (answered is null)
		{
			logger.LogWarning("The XAML provider did not acknowledge the detach, so it may still hold its interfaces.");
			return;
		}

		logger.LogInformation("The XAML provider {Answer} its diagnostics interfaces on detach.", answered);
	}

	/// <summary>
	/// Ends the session in the app and then deletes this session's sandbox folder. The folder is best
	/// effort by nature: the staged provider is loaded into an app that is meant to still be running
	/// afterwards, so the DLL is held open and only the next host's sweep can finish the job.
	/// </summary>
	public void Dispose()
	{
		if (_workDir is null) return;

		// Asked before the pipe goes, because afterwards there is no way to ask and no way to hear
		// the answer. The provider holds an IXamlDiagnostics and an IVisualTreeService per
		// injection and released them from nowhere any caller reaches, so they were held for the
		// life of the app -- and the app outlives the session deliberately, which is what turns a
		// leak per session into a leak that accumulates.
		Release();

		_pipe?.Dispose();
		_pipe = null;

		TryDeleteDirectory(_workDir);
		_workDir = null;
		_stagedProvider = null;
	}

	private void Icacls(string path, string arguments)
	{
		try
		{
			var start = new ProcessStartInfo("icacls.exe", $"\"{path}\" {arguments}")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(start);
			if (process is null) return;

			// Bounded like every other wait on this path. A grant that never finishes is a tool call
			// that never returns, and the AppContainer grants are the last thing between staging the
			// provider and injecting it -- so a wait with no bound here hangs exactly where the pipe
			// has just been logged as listening.
			if (process.WaitForExit((int)Bounds.Grant.TotalMilliseconds)) return;

			logger.LogWarning(
				"icacls {Arguments} on {Path} did not finish within {Seconds}s; the provider may not be able "
					+ "to reach the work folder.",
				arguments,
				path,
				Bounds.Grant.TotalSeconds);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "icacls {Arguments} on {Path} failed.", arguments, path);
		}
	}
	/// <summary>
	/// Which tap serves this target, resolved from the framework DLLs the process has loaded, with
	/// the initialiser bound from the library that tap names (#74, #76).
	/// <para>
	/// Asking first is the whole point. This used to assume UWP in four places, so a WinUI 3 target
	/// spent twenty seconds waiting for an endpoint that was never going to appear and then blamed
	/// the app for not being packaged. The fact that settles it is in the module list, and reading it
	/// costs microseconds.
	/// </para>
	/// <para>
	/// The initialising library is looked up in the target too, rather than loaded by bare name.
	/// UWP's is a system DLL and either route works; WinUI 3's is in a versioned, per-architecture
	/// WindowsAppRuntime framework package on no search path this process has, and the target is the
	/// only thing that knows which copy it is actually running.
	/// </para>
	/// </summary>
	private (XamlTap? Tap, string? Error) ResolveTap(int pid)
	{
		if (_tap is not null && _initialise is not null) return (_tap, null);

		// Re-probed while it is Unknown, which is "could not be determined" rather than "no XAML" and so
		// is not an answer worth keeping. A launched app is asked the moment its session goes Ready, well
		// before it has loaded a XAML framework, and caching that one early look made it the answer for
		// every XAML call the session went on to serve: an app with 116 modules kept being described from
		// the 25 it had at startup. An attached app never showed it, because by then the app is up.
		if (_stack is null or { Stack: XamlStack.Unknown }) _stack = XamlStackProbe.Detect(pid);

		var tap = XamlTaps.For(_stack.Stack);
		if (tap is null) return (null, $"{XamlTaps.NoTapReason(_stack.Stack)} ({_stack.Reason}).");

		// The target's own copy where it has one, and the bare name otherwise -- which is right for a
		// system DLL and is the only thing left to try for anything else.
		var library = XamlStackProbe.ModulePath(pid, tap.InitializeLibrary) ?? tap.InitializeLibrary;

		// Passed through as the tap declares it, which for WinUI 3 is the bare module name rather than
		// a path. Resolving it to the target's full path looks more careful and is not what the
		// framework's own samples do.
		_diagnosticsPath = tap.DiagnosticsModule;

		try
		{
			var handle = NativeLibrary.Load(library);
			var export = NativeLibrary.GetExport(handle, nameof(InitializeXamlDiagnosticsEx));
			_initialise = Marshal.GetDelegateForFunctionPointer<InitializeXamlDiagnosticsEx>(export);
		}
		catch (Exception exception)
		{
			// Its own failure, and said as one. A framework DLL that will not load is not the same as
			// a target with no XAML in it, and reporting it as the latter is what sent the last
			// person looking at their app instead of at their install.
			return (null, $"Could not bind {nameof(InitializeXamlDiagnosticsEx)} in {library}: {exception.Message}");
		}

		_tap = tap;
		return (tap, null);
	}

	/// <summary>
	/// Finds the tap's provider DLL for this host's architecture. The deciding is in
	/// <see cref="XamlProviderPath"/>, where a test can reach it; what is here is the two facts only a
	/// running host knows -- where it is installed and which RID it is.
	/// </summary>
	private static string? ResolveProviderPath(XamlTap tap) => XamlProviderPath.Resolve(new XamlProviderLookup(
		Environment.GetEnvironmentVariable("ROSEMCP_XAML_PROVIDER"),
		AppContext.BaseDirectory,
		RuntimeInformation.RuntimeIdentifier,
		ProviderPlatform(),
		tap.ProviderFileName,
		tap.ProviderProjectName));

	/// <summary>The provider build platform matching this host's architecture (x64 or arm64).</summary>
	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	/// <summary>
	/// <c>InitializeXamlDiagnosticsEx</c> as a delegate rather than a <c>DllImport</c>, because the
	/// library exporting it is per-stack (#74): UWP's is Windows.UI.Xaml.dll, and WinUI 3's is the
	/// WindowsAppSDK's own Microsoft.UI.Xaml.dll, which is not even on the default search path. A
	/// DllImport attribute can name exactly one library, which is the hard-coding this removes.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
	private delegate int InitializeXamlDiagnosticsEx(
		string endPointName,
		uint pid,
		string? wszDllXamlDiagnostics,
		string wszTapDllName,
		Guid tapClsid,
		string wszInitializationData);
}
