# The XAML tap: injection, threading, and teardown

Read before changing `src/RoseMcp.Xaml.Tap/tap_object.h`, injection in `src/RoseMcp.LiveApp/Xaml/`, or anything that advises the visual tree.

- **The tap's node list follows the tree, and a resident tap is the reason that matters.** The walk at
  advise builds it, and every add after that appends. Nothing erased on a remove, which was survivable
  only because a fresh provider per injection re-walked from empty several times a session: re-injection
  was quietly acting as the tree refresh, and nothing said so. Serve reads from the pipe between
  injections and the refresh disappears with them, so the list has to be maintained from the mutation
  stream it is already subscribed to -- otherwise a tree read reports elements the framework has already
  let go, confidently, and an address computed from them resolves to the wrong element. Removals close
  over descendants, because removing a `Border` removes the `TextBlock` inside it and the framework need
  not say so twice; a second removal of a subtree already gone is a no-op, which is what makes the
  belt-and-braces safe.
- **A tap cannot be unadvised while its app goes on being inspected, so a superseded one is stood down
  instead.** The framework creates a provider per injection and asks none of them to stand down --
  `SetSite(nullptr)` is never called -- so every tap a session injects stays advised for the life of the
  app, receiving every mutation in it and appending every add to a tree copy of its own that is never
  cleared. Two tool calls were measured leaving two advised taps receiving 12 and 6 mutations. That is
  megabytes per tool call inside somebody else's application, and under suite load it is worse than a
  leak, because the delivery is on the UI thread and the UI thread is what has to serve the next
  injection.
  <br>
  `UnadviseVisualTreeChange` is not the fix, and this was measured three ways rather than reasoned
  about. It empties the handle map the diagnostics session mints element and value handles from, and it
  leaves the service enumerating nothing for the next callback advised on it. Unadvising the outgoing
  tap after the incoming one walks takes a brush read from `#FF445566` to the handle it was addressed
  by; unadvising before it walks takes the walk itself to zero elements; and keeping one instance and
  advising it again does both, because the framework enumerates for a callback it has not seen and
  leaves one it has registered for nothing. Against a baseline of one failing live-app test, those
  three cost four, seven and nine. Every one of them is a confident wrong answer rather than a failure,
  which is the shape this repository is least willing to ship.
  <br>
  So nothing calls into the framework. A superseded tap keeps its registration, returns from
  `OnVisualTreeChange` at once, and gives back its node list -- which is where all of the cost was.
  Standing down happens on the UI thread, because that is where the callbacks it guards arrive and
  clearing the list from another thread races a walk appending to it. What remains is one tree and one
  handler that does any work, however many injections a session makes, and the removal handling stays
  where #51 needs it: the tap that is answering is still advised between requests, so a selection whose
  element leaves the tree is still noticed.
- **Nothing starts a thread from inside the injection call, and the tap body does not run there
  either.** `SetSite` is reached inline from inside `InitializeXamlDiagnosticsEx` on UWP: a blocking
  cross-process call served by the app's UI thread, arriving while our own DLL is being brought into
  the process. Two things must not happen there. Running the body holds that call open for the length
  of the walk and the toolbar, so everything the app has queued waits behind us, in somebody else's
  application. And starting a thread asks the loader for a lock the injecting thread may be holding,
  which does not fail -- it stops, taking the UI thread and therefore the whole app with it, which is
  exactly the shape of a target that wedges and never recovers.
  <br>
  So the body is *posted* to the UI thread and `SetSite` returns at once. The work still happens on the
  UI thread, because UWP delivers the enumeration to whichever thread asks for it and asking from
  anywhere else puts a second writer on the node list. What moves is only when it happens: after the
  injection call has returned, on an ordinary pump, which is also where the pipe reader's thread is now
  created. Do not "improve" this by giving the body a thread of its own -- that was tried, and the
  first suite run with it produced a wedge with `InitializeXamlDiagnosticsEx` not returning, which had
  not been seen in fifteen runs before or since. WinUI 3 is the opposite case and needs its own thread,
  because its `AdviseVisualTreeChange` enqueues onto the UI thread and blocks the caller; that is why
  where the walk happens is a seam each provider fills rather than a rule this file states.
- **The tap is never ejected from the target.** Visual Studio unadvises once and then unloads its tap
  with `CreateRemoteThread(FreeLibrary)`, and the difference is deliberate rather than unfinished. Our
  overlay stays in the app's visual tree on purpose so the toolbar survives a session, the reader
  thread is running our code, and the framework may still hold the tap as a callback if an unadvise did
  not take. Unloading under any of those is a crash in an application that is not ours, which this
  codebase ranks below a leak. Visual Studio can eject because it tears its whole UI down first. What
  we do instead is give the two framework interfaces back at teardown -- on the detach verb, and on the
  pipe closing under a host that was killed -- and leave the DLL loaded.
- **Injection loads the provider; every request is a message.** The host used to inject per request,
  because the work happens on the app's UI thread and `InitializeXamlDiagnosticsEx` was the only way
  onto it. That made a session's twenty-fourth call its twenty-fourth injection, and since the
  framework never asks a tap to stand down, its twenty-fourth advised sink -- each one receiving every
  mutation in the app, holding a tree copy that only grows, and costing the UI thread that the next
  injection needs in order to be sited at all. One tap for the life of a session is not a thing to
  maintain, it is what falls out of injecting once.
  <br>
  So injection carries no request at all. It stages the provider, loads it, walks the tree and puts
  the toolbar up; everything after that -- the tree, an element's properties, a batch of edits, arming
  and disarming select mode, picking by handle, clearing a pick, and reading what is picked -- is a
  length-prefixed UTF-8 frame on a named pipe the host created and the provider connected back on. A
  reply read from the pipe a request went out on is that request's answer by construction, which is
  what makes the generation stamp unnecessary rather than merely unused: every handshake through the
  folder was "does this file exist", so the host had to number each request and have the provider echo
  it back to tell this answer from the last one.
  <br>
  **A read may fall back to the other channel and a batch may not**, and that asymmetry is the thing
  to preserve if a fallback is ever reintroduced. Asking for a tree twice costs a second answer.
  Sending a batch twice puts a second copy of everything it adds into the app, and a missing reply
  cannot distinguish "never ran" from "ran, and the answer was lost" -- so a batch is reported as
  unanswered, never retried on another channel.
  <br>
  Two things are answered rather than pushed, and both for the same reason: a pick outlives the
  request that armed it, because the person clicks when they click. The mode, the candidate rows and
  the note saying why a selection went away come back in one reply, because read separately they can
  describe a state that never existed at any instant. And arming waits for a layout pass before it
  answers, since a capture layer's extent means nothing until XAML has arranged it -- the wait happens
  on the reader thread, which is not the thread doing the arranging, and a caller already on the UI
  thread must never wait there.
- **The tree walk is advised from a thread that is not the UI thread, and only on WinUI 3 does that
  matter.** WinUI dispatches tap creation onto the UI thread, and its `AdviseVisualTreeChange`
  enqueues the walk *back* onto that thread and then blocks the caller until it finishes
  (`Advising::RunOnUIThread`, with no check for already being on it) -- so advising from `SetSite`,
  which is what UWP wants, deadlocks the one thread that can serve the walk. UWP enumerates inline
  on the calling thread. So the body runs through `RoseTapRunTapBody`, and everything after the walk
  goes back through `RoseTapRunOnUiThread`, because the framework dispatches for the walk alone --
  "during normal operation it is the caller's responsibility to dispatch to the correct thread", in
  its own words. Two things about it are worth keeping. `XamlDiagnostics::Launch` holds the tap only
  in a local `ComPtr`, so advising is also what takes the framework's lasting reference: returning
  from `SetSite` before advising destroys the tap and the body then runs against freed memory, which
  presents as a field reading back a value nothing ever assigned. And the way back onto the UI thread
  is `IXamlDiagnostics::GetDispatcher`, which is a xamlOM method rather than a framework one -- so the
  shared half asks and only the cast is in the provider, a `CoreDispatcher` on UWP and a
  `DispatcherQueue` on WinUI 3. Asking the current thread instead answers only while you are already
  on it, and null everywhere else. That seam is shared with the pipe reader (#50), which needs the
  same thing from the other direction, and the thread id recorded beside it is what lets one function
  serve both: already there, run inline; not there, dispatch and wait.
- **A provider that cannot say why it failed to start costs days, and this one could not.** `Log`
  discarded everything until `SetSite` set the work folder, which is precisely the window in which a
  tap fails to load, fails to be created, or declines to be sited -- so "the framework ignored us"
  and "we were never asked" produced identical evidence: none. The deadlock above was read as the
  former for days on exactly that. It falls back to `%TEMP%` now, and the load, the class-factory
  request and both of `SetSite`'s silent refusals each say so. The refusals have to be *said* rather
  than returned, because `XamlDiagnostics::Launch` discards the `HRESULT` on the reasoning that the
  app must keep running either way. Do not let the pre-`SetSite` path go quiet again.
- **What WinUI 3 was thought to need, it did not.** It does not need diagnostics enabled from
  startup, a session that launched the target rather than attaching, the `XamlDiagnostics` value
  under HKLM, admin, `XAML_DM_*` in the environment, or a packaged app -- every one of those was
  tried, and Visual Studio needs none of them either, which was the clue that the cause was ours.
  It does need its own endpoint name (`WinUIVisualDiagConnection1`), `InitializeXamlDiagnosticsEx`
  out of `Microsoft.Internal.FrameworkUdk.dll` rather than `Microsoft.UI.Xaml.dll`, an **absolute**
  path to the tap (`DebugTool.cpp` fail-fasts on a relative one), and the threading above. Attaching
  to a running WinUI 3 app works, and there is a test for it.
