# UI, tests, and process

**Scope.** `src/RoseMcp.Tray` (MainWindow.xaml + .cs, `WorkspaceRow`, `App.xaml.cs`,
`StartupRegistration`, `TrayOptions`), `src/RoseMcp.Inspector` (`MainWindow`, `Panes/*`,
`Controls/ExecutionBar`, `Program.cs`, `App.xaml.cs`), `src/RoseMcp.Ui.Core` (`SessionRow`,
`Inspector/OperatorClient`, `Inspector/HoldKeeper`, `PollLoop`, `Rows`, `Format`, `ActivityRow`,
the `*Row` types, `Inspector/StopInspection`, `Inspector/XamlInspection`,
`Inspector/XamlTreeBuilder`, `Inspector/InspectedSession`, `Inspector/CommandLine`),
`src/RoseMcp.Ui` (themes, `WindowChrome`, `CrashHandler`); `tests/RoseMcp.UnitTests`,
`tests/RoseMcp.IntegrationTests`, `tests/RoseMcp.IntegrationTests.Windows`,
`tests/RoseMcp.TestSupport`, `tests/fixtures/*`, `tests/apps/*`, `tests/DebugProbeTarget`;
`.github/workflows/{ci,release}.yml`, `tools/*.ps1`, `.editorconfig`, `Directory.Build.props`,
`Directory.Packages.props`, `docs/decisions/` (34), `docs/invariants/` (12).

**Verdict. Strong, with one structural hole and one growing debt.** This is the most deliberate third
of the repository, not the least. The `Ui.Core` / `Ui` split is real and paying: every behavioural
class in the inspector is plain `net10.0` and every one has a test, `HoldKeeper` and `OperatorClient`
are the two most carefully reasoned classes in the product, and the tray reads the live
`WorkspaceManager` rather than a copy exactly as its decision record says. The test architecture's
cost split is exact -- zero `Process.Start`, `MSBuildWorkspace`, `FixtureSolution` or `TestSession` in
884 unit tests that finish in six seconds -- and the live-app suite's phase-and-slot model
(`ProbeConstraints.cs`) is genuinely original work that solved a hard scheduling problem. CI and
`deploy.ps1` are the best-commented files here, and `Assert-WindowsPackage` is a properly structural
release gate. The four tool-surface tests show the repository already knows how to turn a rule into a
mechanism. What holds it back is that it has only applied that trick where a rule has a named type in
`Contracts`. Every rule that is a property of an *arrangement* -- a comment's tense, a published
folder layout, a test class's category attribute, a header's include graph, "every result carries a
revision" -- is review-only, and three of them have already drifted under review: a category lost in
a split, 100 history clauses where #171 counted 90, and four doc claims that describe code that has
moved. The one structural hole is that the newest, least conventional and most bug-dense third of the
product -- debugger, tap, live edit, 55 tests -- never runs in CI at all, which is how #208 came to
be a real "the call reported failure and did the thing anyway" defect found by a test nobody runs on a
schedule. The one growing debt is `TestSession.OpenAsync`: 254 real solution loads and 299 fixture
copies across six distinct fixtures, with zero sharing on the Roslyn half while the live-app half next
door has a proven sharing model. None of this is vibe-coded; it is carefully built and
under-mechanised, which is a much better problem to have.

## Strengths

**The `Ui.Core` / `Ui` split is real and it is paying.** `SessionRow`, `StopInspection`,
`XamlInspection`, `XamlTreeBuilder`, `HoldKeeper`, `PollLoop`, `OperatorClient`,
`InspectorOptions` and every `*Row` are plain `net10.0` and every one of them has a test file
(`tests/RoseMcp.UnitTests/{SessionRowTests,StopInspectionTests,XamlTreeBuilderTests,HoldKeeperTests,PollLoopTests,OperatorClientTests,InspectorOptionsTests,FrameRowTests,ThreadRowTests,PropertyRowTests,VariableNodeTests,InspectorRowTests}.cs`).
This is the single best structural decision in the UI half: the inspector's *behaviour* -- which
element is selected, whether a hold is wanted, whether a read still belongs to the frame it was
asked for -- is all outside WinUI and all under test. The panes that remain are thin controllers.

**`HoldKeeper` is the most carefully reasoned class in the UI**
(`src/RoseMcp.Ui.Core/Inspector/HoldKeeper.cs:141-159`). The `await Task.Yield()` at the top of
`SettleAsync` is load-bearing and the comment says exactly why: it collapses the arriving pane's
claim and the leaving pane's release into one decision, so a tab change never puts the target back
on its own safety timer for a beat. `Want` is declarative and idempotent, so a pane that forgets to
let go is corrected by its own next poll. Nine tests drive it on a deterministic single-threaded
pump (`tests/RoseMcp.UnitTests/HoldKeeperTests.cs`). Under the race the brief asks about -- an agent
continuing while a person reads -- the design holds: `StackPane.Wanted` is `_visible && _stopped`
(`src/RoseMcp.Inspector/Panes/StackPane.xaml.cs:74`), so a resumed target has no reader wanting a
hold, and `MainWindow.Show` calls `AtStop` *before* the panes observe (`:257` then `:262`) precisely
so the yield sees the settled answer.

**`OperatorClient`'s per-request budget** (`src/RoseMcp.Ui.Core/Inspector/OperatorClient.cs:26-49`).
`HttpClient.Timeout` is set to infinite and every call imposes its own budget through a linked
token, *because* `HttpClient.Timeout` raises a `TaskCanceledException` indistinguishable from the
caller's own cancellation -- and a pane closing mid-request must not print "the tray timed out" on
its way out. Three budgets (10s reads, 45s XAML, long-poll wait + 15s margin) with the reason for
each written down. The optional `HttpMessageHandler` makes the whole client testable.

**`PollLoop` over a timer** (`src/RoseMcp.Ui.Core/PollLoop.cs`). Awaits the body before waiting for
the next interval, so a slow host costs a skipped tick rather than a growing queue; resumes on the
starting context so the body may touch bound rows; and the unconditional `await Task.Yield()` at the
top of `RunAsync` is there because a zero-interval loop with a synchronous body would otherwise
never return from `Start()` and the window would be dead on screen. Every one of those is a specific
failure someone hit.

**`XamlTreeBuilder.Merge`** (`src/RoseMcp.Ui.Core/Inspector/XamlTreeBuilder.cs:31-105`) handles a
snapshot that does not describe a tree -- a handle listed twice, a parent nobody listed, a parent
that is its own descendant -- and surfaces each rather than silently dropping the subtree. The
`Placeable` walk carries its own visited set *because the thing it is looking for is a loop*. This
is the opposite of vibe-coded.

**The crash surface is taken seriously.** `App.OnLaunched` has every line inside one `try` with a
comment saying why (`src/RoseMcp.Tray/App.xaml.cs:47-125`); `Fail` builds its own logger factory
because the broker's may not exist yet; `CrashHandler` hooks all three sinks and marks the XAML one
handled with a stated rationale (`src/RoseMcp.Ui/CrashHandler.cs:36-63`). For a process that owns
every warm Roslyn worker on the machine, that is the right call.

**The tray reads live state, not a copy.** `MainWindow.Manager` and `MainWindow.SessionManager`
resolve `WorkspaceManager` and `LiveAppSessionManager` straight out of the in-process broker's
container (`src/RoseMcp.Tray/MainWindow.xaml.cs:119,182`) and `App.xaml.cs:16-21` states this as the
reason the tray hosts the broker at all. There is no second copy to drift and no push protocol. The
`/admin/*` and `/operator/*` endpoints are mapped off the *same* managers (`App.xaml.cs:96-110`),
with a comment saying two hosts serving one broker must not answer different questions.

**`Program.cs` for the inspector is hand-written for two stated reasons** and both are correct: the
single-instance key must be claimed before `Application.Start`, and `CoWaitForMultipleObjects` is
needed instead of `Wait()` because redirection completes through a COM call back into the STA thread
(`src/RoseMcp.Inspector/Program.cs:71-99`). That is a class of bug most apps ship.

## Findings

### Part A -- the UI layer

### UIP-01 `Rows.Merge` breaks the source-order promise in its own docstring, and its test file never asks it to
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Ui.Core/Rows.cs:20` (the promise), `:47-58` (the code), `tests/RoseMcp.UnitTests/RowsMergeTests.cs:40-101`
- **What:** The docstring says "Rows end in the order the sources are in." The second loop only
  *inserts* rows that are new and *updates* rows that already exist -- it never moves one. With
  `rows = [A, B]` and `sources = [B, A]`, both rows are found, both are updated, and the collection
  is still `[A, B]`. The insert is also positionally wrong once an existing row sits ahead of a new
  one: `rows = [C, A]`, `sources = [A, B, C]` puts B at index 1 and yields `[C, B, A]`. Issue #221
  is confirmed. The five tests cover fill, in-place update, removal, insert-past-the-end and empty;
  none of them ever presents a source list whose order differs from the row order, which is exactly
  why this shipped.
- **Why it matters:** Silent and plausible. A reordered list reads as correct data in the wrong
  order, which is the failure class this repository names as the one it is built against. `Recent`
  is documented "newest first" and today survives only because it is prepend-only; the first time
  the broker re-sorts, or a session list is ordered by anything but insertion, the window shows a
  stale order forever, since every subsequent merge also declines to move anything.

  Worse than a plain bug: **the five call sites disagree about what the helper is for, and the
  helper does not say.** `rose_find_references` on `Rows.Merge` returns
  `src/RoseMcp.Tray/MainWindow.xaml.cs:186`, `src/RoseMcp.Tray/WorkspaceRow.cs:216`,
  `src/RoseMcp.Ui.Core/SessionRow.cs:292`, and `src/RoseMcp.Ui.Core/Inspector/InspectedSession.cs:119`
  and `:128`. The last two carry the comment "Merges the host's breakpoints in place, **so a row a
  reader is about to click stays put**" -- which is the *opposite* of the docstring's promise and is
  satisfied only by the current, buggy behaviour. One helper, two contradictory contracts, and the
  code happens to implement the undocumented one.
- **Suggested change:** Settle which contract it has, in the docstring, and give the other its own
  method. Both already exist in this repository: `XamlTreeBuilder.Fill`
  (`src/RoseMcp.Ui.Core/Inspector/XamlTreeBuilder.cs:155-172`) is the ordered one -- remove what is
  gone, then for each wanted index either `Insert` (absent) or `Move` (present elsewhere) -- and
  `Rows.Merge` as written is the stable one. Name them `Rows.MergeOrdered` and `Rows.MergeStable`,
  point the breakpoint and tracepoint lists at the stable one deliberately, and add the missing
  test to each: merge `[1,2,3]`, then `[3,1,2]`, and assert the order each one promises plus
  `Assert.Same` on every row.

### UIP-02 The ordered merge already exists, as a private method of an unrelated class
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Ui.Core/Rows.cs:32-60` and `src/RoseMcp.Ui.Core/Inspector/XamlTreeBuilder.cs:155-172`
- **What:** `Rows.Merge` is the public, documented, "written once rather than per row type" helper,
  and it does not order. `XamlTreeBuilder.Fill` is a `private static` that does exactly the ordering
  `Rows.Merge` promises, including `ObservableCollection.Move` so the tree control keeps expansion
  state through a reparent. The behaviour the shared helper documents is implemented once, in a class
  nothing else can reach.
- **Why it matters:** The repository's stated pattern is one place per rule. Here the rule is stated
  in one place and implemented in another, with nothing pointing a reader between them. Whoever fixes
  #221 will most likely reinvent `Fill` a third time rather than find it.
- **Suggested change:** Promote `Fill` to `Rows.MergeOrdered`, take the key selectors it needs, and
  have `XamlTreeBuilder` call the public one with `row => row`. One implementation of ordering, one
  test file, and the stable variant beside it with its own name and its own test (UIP-01).

### UIP-03 `Rows.Merge` is quadratic, and so is the tree fill
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Ui.Core/Rows.cs:42` (`sources.Any(...)` per row), `:50` (`rows.FirstOrDefault(...)` per source); `XamlTreeBuilder.cs:158,163` (`wanted.Contains`, `into.IndexOf`)
- **What:** Both merges are O(n*m) with a linear scan inside a loop. The activity lists are small, so
  this is invisible today -- but `Rows.Merge` is the general helper and the XAML pane routinely
  reads trees of several thousand nodes, re-read on every live edit.
- **Why it matters:** The merge runs on the UI thread, up to 2.5 times a second in the tray. A
  helper documented as "written once for every list in every RoseMCP window" should not have a
  shape that only works because every list so far has been short.
- **Suggested change:** Build a `Dictionary<TKey, TRow>` of the existing rows once per merge and
  index into it. Same work as the correctness fix in UIP-01, so do them together.

### UIP-04 The tray hand-rolls its own merge because `Rows.Merge` cannot take a comparer
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml.cs:330-350`, `:424` (`Same`)
- **What:** `MergeSessions` (`:185`) calls the shared `Rows.Merge`. `MergeRows` immediately below it
  is a copy-paste of the same algorithm, because the workspace key is a file path that must compare
  `OrdinalIgnoreCase` and `Rows.Merge` is hard-wired to `TKey.Equals`. The tray's copy is worse
  still: it `Add`s rather than inserting at the source index, so it does not even attempt the order
  promise.
- **Why it matters:** Two windows, three merge implementations, one of which will not receive the
  #221 fix because nobody grepping for `Rows.Merge` will find it. A case-sensitive path comparison
  is also a real defect class on Windows: a client that asks with a differently-cased path produces
  a duplicate card.
- **Suggested change:** Add an optional `IEqualityComparer<TKey>? comparer = null` parameter to
  `Rows.Merge` and delete `MergeRows` and `Same`. That is the whole fix, and it removes the only
  reason a second implementation exists.

### UIP-05 `WorkspaceRow` is stranded in the WinUI project by a single enum, so five pure functions cannot be tested
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Tray/WorkspaceRow.cs:3` (`using Microsoft.UI.Xaml.Controls;`), `:284` (`DescribeHealth` returns `InfoBarSeverity`)
- **What:** `WorkspaceRow` is the exact counterpart of `SessionRow`, which lives in `Ui.Core` and has
  a test file. The only thing keeping it out is that `DescribeHealth` returns a tuple containing
  `InfoBarSeverity`. The decision record names this: "What stays in the tray: `WorkspaceRow`,
  because it reaches for `InfoBarSeverity`" (`docs/decisions/debugging-ui.md`). But the consequence
  is not stated there: `ToneOf`, `DescribeState`, `DescribeFacts`, `DescribeHealth` and
  `DescribeRecent` -- the whole of what the tray tells a person about a broken workspace -- are
  untestable, while the identical shapes in `SessionRow` are covered.
- **Why it matters:** These are the sentences a user reads when something has gone wrong, which is
  the moment wording matters most, and they are the only part of the row model with no test. The
  decision made an accidental exclusion sound like a choice.
- **Suggested change:** Declare a four-value `RoseSeverity` enum in `Ui.Core`, return that, and map
  it to `InfoBarSeverity` in one XAML converter or one line of code-behind. `WorkspaceRow` then
  moves to `Ui.Core` beside `SessionRow` and the exception in the decision record disappears rather
  than being documented.

### UIP-06 Four pure `public static` functions sit on a WinUI `Window` where nothing can reach them
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml.cs:356` `DescribeHeadline`, `:375` `DescribeSubtitle`, `:407` `DescribeTooltip`, `:422` `RegistrationCommand`
- **What:** All four are static, pure, take only DTOs and ints, and are `public` -- the signature of
  code somebody intended to test. They are members of `MainWindow : Window` in a
  `net10.0-windows` project, so `RoseMcp.UnitTests` cannot reference them at all. `RegistrationCommand`
  produces the `claude mcp add` line a user copies out of the window; nothing verifies its shape.
- **Why it matters:** This is the same leak as UIP-05 in a more obvious form: the project's own rule
  is that anything a test can see belongs in `Ui.Core`, and `public static` on a `Window` is the
  shape that rule exists to prevent. It is also the surface most likely to be reworded.
- **Suggested change:** Move all four to a `TrayText` static class in `RoseMcp.Ui.Core`, beside
  `InspectorText` which already holds exactly this kind of wording for the other window. Then make
  the rule structural: an analyzer or a review checklist item saying a `public static` member on a
  `Window` or `UserControl` is a mislocation.

### UIP-07 The tray polls with a hand-driven `DispatcherQueueTimer`; the tested `PollLoop` is right there
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml.cs:103-111`, `:152-179`
- **What:** The tray runs two `DispatcherQueueTimer`s and switches the refresh interval by hand
  (`:177-178`), with a comment explaining that setting `Interval` restarts the timer. The inspector
  does the same job with `PollLoop` (`src/RoseMcp.Inspector/MainWindow.xaml.cs:99`), which exists in
  the shared project, has its own tests, and already handles re-entrancy, error reporting and `Kick`.
- **Why it matters:** Two windows, two polling mechanisms. `Refresh()` is also called re-entrantly
  from five `async void` handlers, so an in-flight refresh and a tick can interleave in a way
  `PollLoop` is specifically written to prevent. Low today only because the tray's read is
  in-process and synchronous.
- **Suggested change:** Use `PollLoop` in the tray too, with `Kick()` where the handlers call
  `Refresh()`. The idle/active interval is the one thing `PollLoop` lacks; add a settable `Interval`
  to it rather than keeping a second mechanism.

### UIP-08 A hold is forgotten rather than given back when a stop ends
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Ui.Core/Inspector/HoldKeeper.cs:96-105` (`AtStop`)
- **What:** When the stop sequence moves -- including to zero, which is what a resumed target looks
  like -- `AtStop` sets `_held = false` locally and asks the host nothing. The correctness rests
  entirely on a cross-process invariant: that the host clears its own hold when the target
  continues. Nothing in this process, and no test, asserts it. The keeper is safe today only because
  every reader's `Wanted` is `_visible && _stopped`, so no reader wants a hold on a running target
  and the forgotten hold is never noticed.
- **Why it matters:** If the invariant ever slips -- a summary read that races the stop, a host path
  that ends a stop without clearing the hold -- the outcome is the exact failure the whole class
  exists to prevent: somebody's application frozen for the host's full cap with nothing on screen
  saying why, and a window that believes it holds nothing so will never release it.
- **Suggested change:** When `AtStop` moves off a sequence it believed it held, issue the release
  rather than assuming one. It costs one round trip on a transition that happens seconds apart at
  worst, and it removes a whole class. Add the missing test --
  `A_stop_that_ends_gives_the_hold_back` -- beside the nine that are already there.

### UIP-09 `WorkspaceRow` and `SessionRow` are the same class twice
- **Severity:** Low
- **Effort:** M
- **Where:** `src/RoseMcp.Tray/WorkspaceRow.cs:225-237` vs `src/RoseMcp.Ui.Core/SessionRow.cs:300-321` (the `Tone` enum); `WorkspaceRow.cs:305-310` vs `SessionRow.cs:406-413` (`DescribeRecent`); `WorkspaceRow.cs:211-222` vs `SessionRow.cs:290-298` (the activity `Merge` wrapper, identical but for a comment)
- **What:** Both carry a five-value `Tone` enum with four shared names, `HasRunning`/`HasRecent`/
  `RecentHeader`, an identical private `Merge` over `ObservableCollection<ActivityRow>`, and a
  `DescribeRecent` that differs only in the noun ("recent operation" vs "recent call").
- **Why it matters:** Not a bug; a maintenance tax with a wrong-answer tail. The two `DescribeRecent`
  implementations already disagree about what to call the thing being counted, which is how two
  windows end up describing the same activity list differently.
- **Suggested change:** Extract an `ActivityList` (or a small `RowWithActivity` base) into `Ui.Core`
  holding `Running`, `Recent`, `HasRunning`, `HasRecent`, `RecentHeader` and the merge, parameterised
  by the noun. Depends on UIP-05 landing first.

### UIP-10 Two windows, two copies of three platform interactions
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml.cs:518-533` vs `src/RoseMcp.Inspector/MainWindow.xaml.cs:511-523` (the `ShowInspectorOnAttach` toggle, including its "put it back to what the file says" rationale, written out twice); `Tray/MainWindow.xaml.cs:631-641` vs `Inspector/MainWindow.xaml.cs:480,486,541` (`Process.Start("explorer.exe", "/select,...")`, four sites); `Tray/MainWindow.xaml.cs:312-327` vs `Inspector/MainWindow.xaml.cs:472-492` (open the host log for a session, same fall-back-to-the-folder rule)
- **What:** The tray's `OpenInExplorer` wraps `Process.Start` and narrows the catch to
  `Win32Exception` / `InvalidOperationException` with a comment. The inspector's three sites do not;
  they sit inside a broad `catch (Exception)` that reports to the notice bar. The `/select` argument
  is built by string concatenation in both apps.
- **Why it matters:** `RoseMcp.Ui` exists to be "the half of both windows that is not WinUI ...
  shared so a second window is the same product rather than a lookalike". It currently holds 162
  lines (`WindowChrome`, `CrashHandler`) while three genuinely shared behaviours live twice.
- **Suggested change:** Add `RoseShell.Reveal(path)` / `RoseShell.OpenFolder(path)` and a
  settings-toggle helper to `RoseMcp.Ui`. The quoting and the failure handling then exist once, and
  the "missing file falls back to its folder" rule is written down in one place.

### UIP-11 The panes push state into controls by hand instead of binding, so half the view logic is untestable after all
- **Severity:** Low
- **Effort:** L
- **Where:** `src/RoseMcp.Inspector/Panes/XamlPane.xaml.cs:373-438` (`Show`, `ShowToggles`, `ShowElement`, `ShowDetail`), `src/RoseMcp.Inspector/MainWindow.xaml.cs:279-325` (`ShowTab`, `ShowHeader`), `:381-418` (`ShowEmpty`)
- **What:** The view models in `Ui.Core` are complete and observable, but the panes largely do not
  bind to them -- they read the model and assign `.Text`, `.Visibility`, `.IsChecked` and
  `.IsEnabled` field by field. `MainWindow.ShowEmpty` sets nineteen properties on eleven controls;
  `ShowTab` derives ten control states from `Tabs.SelectedItem`. Binding counts per file bear this
  out: the tray's XAML has 78 binding expressions, the inspector's `ThreadsPane` has 4 and its
  `EventsPane` 13.
- **Why it matters:** "Which pane is visible" and "what this window shows when there is no session"
  is real logic, and it lives in the half no test can reach, which partly undoes the split the
  project chose. It is also where a control added later gets forgotten: `ShowEmpty` and `ShowTab`
  have to be kept in step by hand and are already only nearly in step -- only `ShowTab` moves the
  selection off a XAML tab it has just hidden.
- **Suggested change:** Give each pane a small view state in `Ui.Core` (`XamlPaneState`,
  `InspectorHeaderState`) holding the booleans and strings, and let XAML `x:Bind` to it through a
  `BoolToVisibility` converter. The code-behind then owns only "call the client, hand the answer to
  the model", which is what it is already good at.

### UIP-12 Comment-convention violations in exactly the files the conventions name
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/App.xaml.cs:150` ("which is the state this **used to** be in for every startup failure"), `src/RoseMcp.Inspector/Program.cs:106` ("which is **the old behaviour** and is no worse than refusing to start"), `src/RoseMcp.Inspector/Program.cs:14` ("One inspector per **machine**")
- **What:** Two comments describe what the code was before, which CLAUDE.md forbids by name. The
  third is a stale claim rather than a history note: `Program.cs` says one inspector per machine,
  while `InspectorOptions.InstanceKey` (`src/RoseMcp.Ui.Core/Inspector/InspectorOptions.cs:49-51`)
  keys on the target pid and `src/RoseMcp.Inspector/MainWindow.xaml.cs:20` says "One per process
  rather than one per machine". The type-level doc on the entry point contradicts the code it
  introduces.
- **Why it matters:** The entry-point docstring is the first thing a reader of this app sees, and it
  states the opposite of the design. The two history comments are the failure mode #171 tracks.
- **Suggested change:** "One inspector per debugged process" in `Program.cs:14`. For the other two,
  state the consequence rather than the past: "nothing else can report a startup failure once the
  logger factory itself has failed", and "the app-wide key is the fallback, so a command line that
  does not parse still gets a window".

### Part B -- test architecture

**Unit suite run (this review, 2026-09-17).** `dotnet build tests/RoseMcp.UnitTests -c Debug` then
`tests/RoseMcp.UnitTests/bin/Debug/net10.0/RoseMcp.UnitTests.exe`:
**884 passed, 0 failed, 0 skipped, 5.937 s** (net10.0, arm64). The fast suite is genuinely fast and
genuinely green, and it is 83 files for 884 tests.

**The cost split holds, exactly and structurally.** Grepping `tests/RoseMcp.UnitTests` for
`Process.Start`, `MSBuildWorkspace`, `FixtureSolution`, `TestSession`, `SessionScope` and
`RoseServerProcess` returns **zero hits**. Fifteen unit test files touch disk, which the decision
record explicitly permits and defends. The `linux` CI job publishes the broker and worker for two
RIDs and runs this suite in Release, which is what proves the no-Windows-project claim rather than
inferring it. This is the cleanest boundary in the repository.

### UIP-13 The Roslyn half of the integration suite loads a solution 254 times and copies a fixture 299 times
- **Severity:** High
- **Effort:** L
- **Where:** `tests/RoseMcp.IntegrationTests/TestSession.cs:8-32`, `SessionScope.cs:19-39`, and 31 test classes; `.github/workflows/ci.yml:134-137,181`
- **What:** `TestSession.OpenAsync` constructs a fresh `SolutionLoader` and runs a real design-time
  build on every call. Counted across `tests/RoseMcp.IntegrationTests/*.cs`: **299 `FixtureSolution.Copy`
  and 254 `TestSession.OpenAsync`**, over **six distinct fixtures** -- `Members` 165 times, `Simple`
  91, `MultiType` 17, `WithGenerator` 15, `XamlStub` 9, `Siblings` 1. Issue #39 says 58 workspace
  loads; the real number today is four times that and still climbing. `MemberEditTests` alone opens
  59. There is not one `ClassDataSource`, `[Before(Class)]` or shared workspace anywhere on the
  Roslyn half -- every sharing attribute in the project is on the live-app probe apps
  (`LiveAppUwpTests.cs:27`, `LiveAppWinUiTests.cs:21`, `LiveAppUwpModernTests.cs:18`).
- **Why it matters:** This is the whole reason `dotnet test` takes minutes, and the reason the CI
  comment at `ci.yml:134-137` has to warn that the job is slow "rather than merely long" and cap
  parallelism at 3. It also shapes behaviour: a suite that costs minutes is one nobody runs before
  pushing, so the integration tests stop being a feedback loop and become a gate. The cost grows
  linearly with every new test, which is exactly the slope `the-live-app-suite-is-phased-by-what-tests-share`
  was written to flatten for the other half.
- **Suggested change:** The machinery is already in the repository and already proven -- apply the
  live-app half's own answer to the Roslyn half. Three layers, in order of payback:
  1. **A read-only shared workspace per fixture.** A `LoadedFixture<TMembers>` /
     `LoadedFixture<TSimple>` with `[ClassDataSource<...>(Shared = SharedType.PerAssembly)]`, handed
     to every class that only reads: `OutlineTests` (10), `NavigationTests` (21), `ResolveNameTests`
     (13), `ImplementationTests` (6), `DiagnosticsTests` (4), `GeneratedDocumentTests` (4),
     `CodeFixTests` list-only. That is roughly 60 loads collapsed to 2, with no behaviour change,
     since nothing in those classes writes.
  2. **A warm copy for the classes that mutate.** `FixtureSolution.CopyTree` deliberately drops
     `bin` and `obj` for the "fresh clone" property (`FixtureSolution.cs:98`), and that property is
     only load-bearing for the generator tests. Add `FixtureSolution.CopyWarm`, which copies a
     once-restored template including `obj/`, so the other ~190 copies skip restore. Keep `Copy` and
     have `WithGenerator` and `BuildFreshnessTests` keep using it, with a comment saying why.
  3. **Serve editing tests from a pool.** The live-app suite's slot model
     (`ProbeConstraints.cs:26-89`) is exactly this shape: a pool of N scratch workspaces, a
     `NotInParallel` key per slot, and a hand-back check. Apply it verbatim.
  Do (1) first and measure -- it is a day's work, cannot change any assertion, and removes the
  largest single block.

### ~~UIP-14 `LiveAppInspectionTests` lost its `[Category("LiveApp")]` in the split, so eleven debugger tests now run in CI that CI says it does not run~~
**#295.** Eleven debugger tests were running in CI that CI said it did not run, because the category
that excludes them is applied by hand. Kept and widened -- the exclusion now names the toolchain it
is about, a test decides which half a class is in, and CI runs 33 debugger tests rather than 11.

### UIP-15 Issue #208's flake is structural, and the structure is in the product, not the test
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Xaml/XamlProviderPipe.cs:176-253` (the host's bound), `src/RoseMcp.Xaml.Tap/tap_object.h:282` (`selecthandle` dispatched with `RoseTapRunOnUiThread`)
- **What:** `XamlProviderPipe.Request` imposes a timeout on each step and returns `TimedOut(...)`. It
  is a *latency* bound on the host side only: the request has already been written to the pipe, and
  `tap_object.h` dispatches every handler onto the app's UI thread with `RoseTapRunOnUiThread`, which
  nothing on the host side can cancel. So a `selecthandle` the host has given up on still executes
  in the app, later, and re-publishes the selection -- which is exactly the symptom in #208 (the
  test's own assertions pass, the turn's hand-back reads again milliseconds later and finds
  `Selected == true`). The issue's own hypothesis is correct, and it says the right thing about its
  own scope: "a XAML request the host has timed out on still runs in the app, so a caller's next
  read can observe the effect of a call that reported failure."
- **Why it matters:** This is not a flaky test. It is "the tool reported failure and did the thing
  anyway", which is the confident-wrong-answer class this whole product is built against, and an
  agent is the caller least equipped to notice. The test is the only reason anybody knows.
- **Suggested change:** Give the protocol a request id and have the tap check, on the UI thread just
  before it acts, that the host has not abandoned the request -- or at minimum make abandonment
  poison the channel so the next read cannot silently observe a stale mutation. Then keep the test
  as the regression guard. Separately (UIP-16), stop the fixture's hand-back check racing the same
  system.

### UIP-16 A fixture hand-back check that re-reads live state will flake whatever the product does
- **Severity:** Medium
- **Effort:** S
- **Where:** `tests/RoseMcp.IntegrationTests/UwpProbeApp.cs:501-526` (`SessionTurn.DisposeAsync`, the hand-back assertion at `:524`), and the slot hand-back rule in `docs/invariants/live-app-tests.md`
- **What:** The hand-back check reads the app's selection *again*, after the test's own read, and
  fails the test if it is not clean. It is asserting against an asynchronous system that a second
  party (the app's UI thread) can still change. #208 is the first case; it will not be the last,
  because the check is structurally a second sample of a value that moves.
- **Why it matters:** The invariant doc is right that residue must fail the test that left it --
  that is what turned three costumed bugs into one. But a check that can fail for reasons other than
  residue converts a product race into a *test* failure attributed to the wrong test, which is the
  exact confusion the rule exists to remove.
- **Suggested change:** Make the check quiescent before it samples: drain or fence the app's UI-thread
  queue (a round-trip no-op request the tap dispatches through the same queue answers this exactly),
  then read. Then a dirty hand-back means residue and nothing else.

### UIP-17 Two-thirds of the integration suite tests the service layer, so the tool boundary's own invariants are spot-checked rather than enforced
- **Half done, #295.** Attribution is structural rather than tested: the forwarding path will not
  compile with a result the broker cannot attribute. The runtime half -- that a tool populates those
  fields against a real workspace -- still wants UIP-13's shared fixture.
- **Severity:** Medium
- **Effort:** M
- **Where:** 20 of 40 integration classes call a `*Service.*Async` directly (`OutlineTests.cs:17`,
  `NavigationTests`, `MemberEditTests`, ...); only `BrokerTests`, `RelayTests`, `OperatorApiTests`,
  `ToolParityTests`, `LiveAppSessionTests` and `LiveAppInspectionTests` go through a tool call.
  Attribution is asserted in `BrokerTests.cs:53-54,717-718,735` and `Revision` in four files total.
- **What:** Testing at the service seam is the right call for a refactor -- it is a stable boundary
  and it keeps the tests readable. But CLAUDE.md states two of its five global rules *at the MCP
  boundary*: "Every result carries a `revision` and names the workspace that answered. Attribution is
  added once, in `WorkspaceManager`, so a tool added later cannot forget it", and "convert at the MCP
  boundary, never at the throw site". Neither is asserted for the ~45 tools; three tools are
  spot-checked in `BrokerTests`.
- **Why it matters:** The rule says a tool added later *cannot* forget attribution, but nothing
  proves it. A refactor that moved or bypassed the attribution wrapper -- which is precisely the kind
  of change a broker refactor makes -- would pass the whole suite.
- **Suggested change:** One reflective test in the pattern this repo already uses four times
  (`ToolSurfaceTests`, `ToolDescriptionTests`, `ToolBudgetTests`, `SecurityModelTests`): enumerate
  every advertised tool, call it against a shared fixture with minimal valid arguments, and assert
  the result carries a non-zero `revision` and the expected `workspace`/`workspaceKey`. It piggybacks
  on the shared fixture from UIP-13 and costs one load.

### UIP-18 The stdout rule -- the one that corrupts the protocol -- has no guard of its own
- **Severity:** Medium
- **Effort:** S
- **Where:** `CLAUDE.md` "Rules that bind everywhere"; `tests/RoseMcp.IntegrationTests/RoseServerProcess.cs:26-30`
- **What:** "Nothing writes to stdout in stdio mode except protocol frames" is the rule whose
  violation is hardest to diagnose, and the only thing enforcing it is that `RoseServerProcess`
  parses the child's stdout as JSON-RPC, so garbage there fails `BrokerTests` for a reason that reads
  as a protocol bug. The review's own ground truth ("No `Console.Write` in `src`") was established by
  hand.
- **Why it matters:** It is a one-line grep, it is the rule stated first in CLAUDE.md, and it is the
  one a new contributor or an agent is most likely to break while debugging.
- **Suggested change:** A unit test that reflects over every launchable host assembly for calls to
  `Console.Write*`/`Console.Out`, or -- cheaper and honest -- a CI step:
  `! grep -rn 'Console\.Write\|Console\.Out' src --include=*.cs`. The same step can carry the comment
  grep #171 asks for (see UIP-23).

### UIP-19 The unit suite is 83 files in one flat folder with one namespace
- **Severity:** Low
- **Effort:** S
- **Where:** `tests/RoseMcp.UnitTests/` (no subdirectories), every file `namespace RoseMcp.UnitTests;`
- **What:** `src` is organised by concern into eighteen projects, and `.editorconfig` escalates
  IDE0130 so a namespace must match its folder. The test project that covers all of it is flat:
  `XamlStubTests` beside `HoldKeeperTests` beside `SolutionResolverTests` beside `PortablePdbTests`.
  The integration project is the same (50 files, flat).
- **Why it matters:** Finding the tests for a subsystem means guessing at file names, which is the
  single most common thing an agent does before changing code here. It also makes "does this
  subsystem have tests" unanswerable at a glance -- which is the question the invariant table below
  is trying to answer.
- **Suggested change:** Folders mirroring the projects they cover (`Broker/`, `Worker/`, `Symbols/`,
  `Ui/`, `Contracts/`, `XamlDiff/`), with namespaces to match so IDE0130 keeps them honest. Purely
  mechanical, and it makes the next split cheaper rather than more expensive.

### UIP-20 Named gaps stay open because nothing makes an untested capability visible
- **Severity:** Medium
- **Effort:** M
- **Where:** #77 (`IsAppXaml` unverified on WinUI 3, untested on WinUI 2), #98 (a properties read
  never reports `TypeName`, and nothing asserts it), #153 (no fixture pinned to an older SDK, so
  `BuildHost`'s behaviour under one is unverified)
- **What:** Three capabilities that ship and are untested, each open for weeks. #98 is the sharpest:
  a field that is always absent from a result, which is the confident-wrong-answer shape -- a caller
  reads "no type name" as a fact about the element. #77 is a decision procedure (which markup is the
  app's own) whose wrong answer silently changes what a pick returns. #153 is the one that will be
  found by a user rather than by the suite.
- **Why it matters:** Not that they are open -- they are filed, which is the culture working -- but
  that nothing surfaces them. The security model has `SecurityModelTests` to notice a tool with no
  entry; there is no equivalent for "a result field nothing ever asserts".
- **Suggested change:** For #98 specifically, a golden-shape test per result DTO: build one instance
  from a real call and assert every non-nullable property is populated and every documented-optional
  one is populated at least once across the suite. That single pattern catches #98 and the next
  three like it. For #77, the fix is a WinUI 2 fixture app in `tests/apps/`, which the apps README
  already anticipates with its "planned" row.

### UIP-21 `xunit.v3.assert` on TUnit is the right choice and should be written down before somebody "fixes" it
- **Severity:** Low
- **Effort:** S
- **Where:** `tests/RoseMcp.UnitTests/RoseMcp.UnitTests.csproj:15,42-44`, `tests/RoseMcp.IntegrationTests/RoseMcp.IntegrationTests.csproj`
- **What:** Both test projects take `xunit.v3.assert` as a package and alias `Xunit.Assert` to
  `Assert` via a global using, because TUnit also exposes an `Assert` and an unqualified one would be
  ambiguous. The csproj comment explains the alias but not the choice.
- **Why it matters:** It looks like a leftover from an xunit migration, and it is the kind of thing a
  cleanup pass removes. It is in fact a good call -- xunit's assertions are terser and more
  informative than TUnit's fluent `await Assert.That(x).IsEqualTo(y)` for a suite this size, and
  mixing the two would be worse than either. The repository writes down choices this size elsewhere
  (34 decision records).
- **Suggested change:** A one-paragraph decision record, `assertions-come-from-xunit-not-tunit.md`,
  saying what was chosen and what the alternative costs. Also note the same file carries a history
  comment ("These **used to** sit in the `RoseMcp.Worker.Tests` namespace") which the #171 grep will
  never see, because it only looks at `.cs`.

#### Which invariants have a guarding test, and which are review-only

Built by grepping the two test projects for each rule's own key type or word, then reading what the
hit asserts. "Structural" means a test that fails when a *new* thing forgets the rule; "by example"
means tests that would catch a regression in existing code but not an omission in new code.

| Rule (CLAUDE.md / `docs/invariants/`) | Guard | Kind |
|---|---|---|
| Nothing writes to stdout in stdio mode | none directly; `RoseServerProcess` parses the child's stdout, so garbage fails `BrokerTests` for the wrong stated reason | **review-only** (UIP-18) |
| Reads never observe a snapshot older than disk (`WorkspaceSession` barrier) | `StalenessTests`, `NewFileTests`, `DiskSynchronizerTests`, `SolutionWatcherTests`, and 17 classes that go through `WorkspaceSession` | by example, strong |
| Every result carries a `revision` and names the workspace | `BrokerTests.cs:53-54,717-718,735` on three tools; `Revision` asserted in four files total | **review-only** for ~42 of ~45 tools (UIP-17) |
| An error says what went wrong; convert at the MCP boundary | `ForwardedErrorTests`, `ArgumentValueTests`, `ToolDescriptionTests` | by example |
| Whatever writes C# ends formatted, both passes | `WhitespaceTests`, `LineEndingsTests`, `BodyEditTests`, `MemberSyntaxTests` plus assertions inside six edit-service classes | by example, strong |
| `transport-and-lifetime` | `BrokerTests`, `RelayTests`, `RelayFixture`, `RelayRetryPolicyTests`, `ResilienceTests`, `ProgressReportingTests`, `LoopbackOriginTests` | by example, strong |
| `workspace-freshness` | as the barrier row above | by example, strong |
| `solution-routing` | `SolutionResolverTests`, `WorkspaceRoutingTests`, `BuildPropertiesTests`, `SolutionFileReaderTests` | by example, strong |
| `result-shapes` | `ToolSurfaceTests`, `ToolDescriptionTests`, `ToolBudgetTests`, `SecurityModelTests`, `ToolArgumentShapeTests`, `ToolParityTests` | **structural** -- the best-guarded rule in the repo |
| `writing-csharp` | as the formatting row | by example, strong |
| `analyzers-and-generators` | `AnalyzerLockTests`, `SolutionLoaderTests`, `WorkspaceStatusTests`, `XamlStubTests`, `WinUiXamlStubTests`, `WpfXamlStubTests`, `XamlStubChannelTests` | by example, strong |
| `xaml-live-edit` | `XamlDiffTests`, `XamlApplyBaselineTests`, `XamlMaterialiserTests` (fast) + `LiveAppUwpTests` (never in CI) | by example; the apply half is CI-uncovered |
| `tap-tiers` (which header may name what) | **none** -- a pure include-graph rule over C++ headers, checked by review | **review-only** |
| `xaml-tap-lifecycle` | `LiveAppUwpTests`, `LiveAppWinUiTests` only -- never run in CI | **review-only in practice** |
| `overlay` | `LiveAppUwpTests` only -- never run in CI | **review-only in practice** |
| `hosts-and-deploy` | `PublishedLayoutTests`, `RepositoryHostBuildTests`, `XamlStackModulesTests`, `TargetArchitectureProbeTests` for the C# resolvers; `Assert-WindowsPackage` in `deploy.ps1` for the package | split across two mechanisms that never meet (UIP-25) |
| `live-app-tests` (hand-back, slots, one gate) | the fixtures assert it themselves (`UwpProbeApp.SessionTurn.DisposeAsync`) | **structural**, and the best idea in the test suite |
| Conventions: tabs, file-scoped namespaces, Allman, IDE0130 | `.editorconfig` + `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` + `dotnet format --verify-no-changes` in CI | **structural** |
| Conventions: braces on a next-line body; comments carry no history or issue tags | `csharp_prefer_braces = when_multiline` gets part of the first; nothing gets the second | **review-only** (UIP-23) |

The pattern is clear and worth stating: **every rule that has a named type in `Contracts` has a
structural guard, and every rule that is a property of an arrangement -- a header's include graph, a
comment's tense, a published folder layout, a category attribute -- has none.** The four
tool-surface tests show the repository already knows how to close that gap; it has just not been
applied outside `Contracts`.

### Part C -- process and docs as a maintenance system

**What runs where, as of today.**

| | Pull request | Push to main | Never in CI |
|---|---|---|---|
| `build-and-test` (Windows) | build, `dotnet format --verify-no-changes`, unit suite Debug | same | |
| `linux` | publish broker + worker for `linux-x64`/`linux-arm64`, unit suite Release | same | |
| `integration` | only if a changed file is outside `docs/ wiki/ tools/ .claude/ src/*.Tap/ release.yml *.md`; `--maximum-parallel-tests 3`, `[Category!=LiveApp]`, plus `IntegrationTests.Windows` | always | |
| `xaml-providers` | only if a tap folder / `Directory.*.props` / `global.json` / `ci.yml` changed; **x64 Debug only**, compile and link, no tests | all six of {Uwp,WinUi} x {x86,x64,arm64} as Release | |
| live-app suite | | | **`LiveAppSessionTests` (19), `LiveAppUwpTests` (25), `LiveAppWinUiTests` (8), `LiveAppUwpModernTests` (2), one `OperatorApiTests` method -- 55 tests** |
| `tools/*.ps1` (1,496 lines) | | | never linted, never executed except `deploy.ps1 -Mode package` on a tag |

The CI file is the best-commented thing in the repository: the fail-open `!= 'false'` condition, the
`paths:`-versus-`if:` reasoning about required checks, the two-commit checkout, the
`--maximum-parallel-tests 3` measurement, and the "a missing toolset is a warning on a laptop and a
failure on CI" rule are each a specific failure someone paid for. The release job is equally careful:
MinVer off the tag so there is one place to get the version right, two runners because only a Linux
tar records an execute bit, and `Assert-WindowsPackage` gating the artifact.

### UIP-22 `PublishedLayoutTests` guards a layout it stages itself, not the one `deploy.ps1` writes
- **Severity:** Medium → **High**
- **Effort:** M
- **Where:** `tests/RoseMcp.UnitTests/PublishedLayoutTests.cs:32-42` (`Stage(...)` by hand, docstring "The shape `Publish-Tree` writes"), `tools/deploy.ps1:131` (`Publish-Tree`), `:396` (`Assert-WindowsPackage`)
- **Scope grew with PR #277.** The layout had two parties when this was filed. It now has five:
  `tools/deploy.ps1`, `tools/RoseMcp.Deploy.ps1`, `tools/build-installer.ps1`, `installer/install.ps1`
  and `installer/rosemcp.iss` — the last two being **packaged content a user runs**, not a script this
  repository runs. The installer work was careful about the part it could see (one staged tree feeds
  both the zip and the setup exe, so they carry identical bytes, and `RoseMcp.Deploy.ps1` holds what
  all three agree about for stopping a running install). What it could not do is join any of that to
  the C# resolvers, which is this finding. A layout change now passes every C# test and fails on a
  stranger's machine at install time.
- **What:** The test builds a directory tree from nine hard-coded paths and asserts the C# resolvers
  find things in it, with `searchRepository: false` so the development fallback cannot mask a break.
  That part is excellent. What it does not do is read anything `deploy.ps1` produces: if `Publish-Tree`
  moves the tray, renames `live-app/<rid>/`, or stops publishing the inspector, the test stages the
  old shape and passes. The docstring even names the coupling -- "the shape `Publish-Tree` writes" --
  and there is nothing but that sentence holding the two together. `Assert-WindowsPackage` covers the
  other half (both windows, a host per architecture, both providers, and each PE's machine type) but
  knows nothing about whether a resolver can find any of it.
- **Why it matters:** The failure class is named in the test's own docstring -- "three defects lived
  only on an install" -- and the guard that was built for it is half of a pair with no join. A layout
  change made in PowerShell passes every C# test and fails only when somebody runs the install.
- **Suggested change:** Make one of them the source. Cheapest: have `Publish-Tree` write a
  `layout.json` listing every path it produced, have `Assert-WindowsPackage` validate against it, and
  have `PublishedLayoutTests` stage from a committed copy of that file with a test that the committed
  copy matches what a `-Mode package` run emits. More direct: have the test shell out to
  `deploy.ps1 -Mode package -SkipBuild` into a temp root. Either way the arrangement stops being a
  fact two files remember separately.

### ~~UIP-23 The comment conventions are unenforced and the debt is growing, not shrinking~~
**#295.** The comment conventions bound every file and nothing checked them, so the debt only grew.
CI checks them against a per-file baseline that may only go down. **The measurement was wrong, and
that is the more useful half:** three of the phrases this finding counted are not history clauses at
all, and paying the rest is #171's work.

### UIP-24 Nothing formats or lints the C++ or the PowerShell
- **Severity:** Low
- **Effort:** S
- **Where:** no `.clang-format` anywhere in the repository; `.editorconfig` has no `[*.{h,cpp}]` section; `tools/*.ps1` (1,496 lines with `build.ps1` x2) and no PSScriptAnalyzer step
- **What:** `dotnet format` covers `*.cs` only, and CI's `xaml-providers` job compiles the taps
  without checking anything about how they are written. The C++ is four tiers of shared headers
  (`docs/invariants/tap-tiers.md`) whose whole design is an include-graph discipline, and the only
  thing enforcing either the discipline or the formatting is review. The same is true of
  `tools/deploy.ps1` (562 lines), which is the release path.
- **Why it matters:** Small on its own; it matters because `tap-tiers` is a *structural* rule
  expressed only in prose, and because `deploy.ps1` has already grown a comment about `-f` binding
  tighter than `+` (`:370-372`) -- the kind of thing a linter catches and a reviewer does not.
- **Suggested change:** A `.clang-format` matching the C# conventions (tabs, Allman) and a
  `clang-format --dry-run -Werror` step in the `xaml-providers` job, which already has the toolset.
  `Invoke-ScriptAnalyzer` over `tools/` and `src/**/build.ps1` in the same job, five lines. For
  `tap-tiers` itself, a small script asserting the allowed `#include` edges is a better guard than
  either -- the tiers are a graph, and a graph is checkable.

### UIP-25 The newest third of the product -- debugger, tap, live edit -- has no CI coverage at all
- **The debugger third is done, #295**, by splitting the suite on what each test needs. What is left
  is the XAML, C++ and UWP half -- card 16's self-hosted runner -- and the flake rate nothing measures.
- **Severity:** High
- **Effort:** L
- **Where:** `.github/workflows/ci.yml:129-132,181` (category exclusion), `:238-264` (providers compile only)
- **What:** 55 tests across four classes and one method never run in CI. The `xaml-providers` job
  compiles both taps but runs nothing against them. So the ICorDebug session, the injection, the
  visual tree, the overlay, the pick, the properties read and the live-edit apply are verified only
  when one person runs the suite on one machine with a C++ toolset, the Windows App SDK and developer
  mode. The exclusion is well-reasoned in the file -- a hosted runner genuinely lacks the toolchains,
  and skips reading as passes is worse -- but the consequence is that the invariants `overlay.md`,
  `xaml-tap-lifecycle.md` and half of `xaml-live-edit.md` are review-only in practice, and
  `live-app-tests.md`'s own hardest-won rule ("green once is not green") cannot be applied at all,
  because nothing samples repeatedly.
- **Why it matters:** This is the half the review brief calls "largely vibe-coded", it is the half
  with the most open bugs (8 `live-app` labels), and it is the half with the least automated
  evidence. #208 is exactly what that combination produces: a real product defect found by a test
  nobody runs on a schedule.
- **Suggested change:** A self-hosted runner is the honest answer and the expensive one. Short of
  that, two things that cost little and recover most of the value: (1) a scheduled `workflow_dispatch`
  / nightly job on the developer machine's own runner, or a documented `./tools/Rose.ps1 live-app`
  that runs the suite N times and reports a flake rate -- the measurement `live-app-tests.md` says is
  required and that nothing currently produces; (2) split the live-app suite by what it actually
  needs, as `LiveAppInspectionTests` accidentally demonstrates -- the ICorDebug half needs only a
  .NET process and *can* run on a hosted Windows runner (see UIP-14). Doing (2) deliberately would
  move roughly a third of those 55 tests into CI today.

### UIP-26 The docs are strong and the index has already drifted: twelve spot-checks, seven hold
- **Severity:** Medium
- **Effort:** M
- **Where:** `CLAUDE.md` (the Contracts list, the Architecture table), `docs/decisions/debugging-ui.md`, `docs/invariants/hosts-and-deploy.md`, `docs/invariants/live-app-tests.md`
- **What:** Twelve claims checked against the code (table below): seven hold
  exactly, one is materially right with a wrong enumeration, and four are stale. The stale ones are
  the same shape: a *count* or a *current state* written into a document whose job is to state a
  rule. `debugging-ui.md` says "the 489-test unit suite" (it is 884). `hosts-and-deploy.md` says
  "`DetectArchitecture`'s `LaunchUwp => X64` now answers confidently and wrongly", but
  `LiveAppSessionManager.cs:290` now reads the package identity via `UwpArchitecture(aumid)` -- the
  defect is fixed and the invariant still describes it as present. `CLAUDE.md` names seven types as
  "the whole list" of logic in `Contracts`; `ArgumentValues` is an eighth, a page of parsers with
  refusal messages, added since. `live-app-tests.md` teaches its serialisation lesson through
  `[TestClass(DisableParallelization = true)]`, an xunit attribute that appears nowhere in the
  repository -- the suite is TUnit and uses `NotInParallel` keys.
- **Why it matters:** The structure is right and unusually good; the failure mode is specific and
  fixable. Every drifted claim is a measurement or a present-tense state, and CLAUDE.md's own
  convention already forbids exactly that in comments -- "a number with no decision hanging on it is
  a story". The rule is not applied to the documents.
- **Suggested change:** Extend the comment convention to `docs/`: a decision or invariant states a
  rule and its failure mode, never a count and never "now X". Where a number is load-bearing, cite it
  as the measurement that decided something and say when. Then fix the four above.

### UIP-27 Three things the docs do not have, and an agent needs all three
- **Severity:** Medium
- **Effort:** M
- **Where:** `CLAUDE.md` (the index), `docs/decisions/` (34 files), `docs/invariants/` (12 files)
- **What:** The system is decisions + invariants + wiki with CLAUDE.md as the index, and it works: the
  invariant table is keyed by *what you are about to touch*, which is the right index for an agent,
  and 34 decision records named for the decision rather than numbered is a better scheme than ADR
  numbering. Three things are missing and each one costs an agent a search:
  1. **A tool -> host -> process map.** There are ~45 tools across four hosts (broker, worker,
     live-app host, and five host-internal ones reachable only via `/operator`). Nothing says which
     host owns which tool, which means "where do I add a tool" is answered by reading `ToolNames.cs`,
     `WithTools` registrations in three places, and `ToolNames.LiveAppPairs`.
  2. **A process-boundary list.** The review's own ground truth counts twelve IPC boundaries, each
     with a different protocol and framing. Nothing in `docs/` enumerates them, so "what crosses this
     line and how" is derived fresh each time.
  3. **A glossary.** `workspace` (a loaded solution) versus `workspace key` (a short hash) versus
     `WorkspaceSession` (the barrier) versus `session` (a debug session) versus `SessionScope` (a test
     fixture) versus `CallSession` (an MCP transport session) -- six meanings of two words, all
     load-bearing, all defined only where they are used.
- **Why it matters:** These are the three questions an agent asks before its first edit, and all
  three are currently answered by reading source. The invariants file already proves the pattern
  works when the index is keyed by the reader's intent.
- **Suggested change:** `docs/map.md` with those three tables, linked from CLAUDE.md beside the
  invariant table. Generate the tool table from `ToolNames` in a test so it cannot drift -- the same
  trick `SecurityModelTests` already plays on the security model.

#### Documentation spot-checks

| Claim | Where it is written | Verdict |
|---|---|---|
| The five stop-inspection tools are "deliberately absent from `ToolNames.LiveAppPairs`, so the parity test fails if anyone adds one there without a broker tool to match" | `docs/decisions/debugging-ui.md` | **holds** -- `src/RoseMcp.Contracts/ToolNames.cs:111` carries the `<see cref="LiveAppPairs"/>` note, `tests/RoseMcp.IntegrationTests/ToolParityTests.cs:35-37` iterates the dictionary (confirmed with `rose_find_references`) |
| Only `build.ps1`'s exit code 3 skips a test; any other non-zero fails it with the build's output | `docs/decisions/only-a-missing-toolchain-skips-a-test.md` | **holds** -- `tests/RoseMcp.IntegrationTests/TestToolchain.cs:130-136` |
| `DebugProbeTarget`'s self-termination "is ten minutes now" | `docs/invariants/live-app-tests.md` | **holds** -- `tests/DebugProbeTarget/Program.cs:29` |
| `Get-LiveAppRuntimes` is the one place that says which hosts an install needs | `docs/invariants/hosts-and-deploy.md` | **holds** -- `tools/deploy.ps1:161`, one definition, two callers (`:196` publish, `:331` `Assert-WindowsPackage`) |
| `ToolSurfaceTests` asserts the advertised set on each operating system | `docs/decisions/only-the-live-app-half-is-windows-only.md` | **holds** -- `tests/RoseMcp.UnitTests/ToolSurfaceTests.cs:177` branches on `OperatingSystem.IsWindows()` |
| The inspector "references `RoseMcp.Ui`, `RoseMcp.Ui.Core`, `RoseMcp.Contracts` and the logging sink, and nothing that loads a solution" | `docs/decisions/the-inspector-is-a-client-of-the-broker.md` | **drifted, materially right** -- `src/RoseMcp.Inspector/RoseMcp.Inspector.csproj:36-39` references Settings, Logging, Ui and Ui.Core; `Contracts` is transitive and `Settings` is unlisted. The load-bearing half ("nothing that loads a solution") holds |
| "the 489-test unit suite covers them on Linux" | `docs/decisions/debugging-ui.md` | **drifted** -- 884 tests today |
| "`DetectArchitecture`'s `LaunchUwp => X64` now answers confidently and wrongly (#117)" | `docs/invariants/hosts-and-deploy.md` | **drifted** -- `src/RoseMcp.Broker/LiveAppSessionManager.cs:290` now calls `UwpArchitecture(aumid)`, which reads the package identity; the invariant describes a fixed defect as current |
| `XamlStackModules`, `ToolArgumentShape`, `XamlProviderPath`, `ValuePath`, `SymbolLocation`, `HostVersion` and `BreakpointCondition` "are the whole list" of logic in `Contracts` | `CLAUDE.md` | **drifted** -- `src/RoseMcp.Contracts/ArgumentValues.cs` is an eighth, and a substantial one |
| "`[TestClass(DisableParallelization = true)]` reads as 'not in parallel with each other'..." | `docs/invariants/live-app-tests.md` | **drifted** -- no such attribute exists anywhere; the suite is TUnit and expresses this with `NotInParallel` keys (`ProbeConstraints.cs`). The lesson is still correct and still applies |
| `RoseMcp.UnitTests` runs no MSBuild, starts no child process, needs no fixture solution, runs on Linux | `docs/decisions/the-test-split-is-by-cost-and-dependency-not-by-disk.md`, `CLAUDE.md` | **holds** -- zero hits for `Process.Start`/`MSBuildWorkspace`/`FixtureSolution`/`TestSession`/`SessionScope`/`RoseServerProcess`; 884 tests in 5.9 s; the `linux` CI job runs it |
| "Removals among siblings are applied last-first", and "the unit test pins the order rather than the outcome" | `docs/decisions/sibling-removals-are-applied-last-first.md` | **holds** -- `tests/RoseMcp.UnitTests/XamlDiffTests.cs:308` |

## Pit-of-success inversions

Each is a rule that today depends on a person remembering it, and a mechanism that would make
forgetting it impossible. The repository already does this four times for the tool surface
(`ToolSurfaceTests`, `ToolDescriptionTests`, `ToolBudgetTests`, `SecurityModelTests`); every
inversion below is that same trick applied somewhere else.

**1. "Testable UI logic lives in `Ui.Core`" -> an analyzer or a test that a WinUI type has no public
static members.** Today the rule is honoured by intention and broken in two places
(`MainWindow.DescribeHeadline` and friends, `WorkspaceRow`'s five `Describe*`). `public static` on a
`Window` or `UserControl` is the exact syntactic shape of the violation, so a Roslyn analyzer or a
reflective test over the two WinUI assemblies catches every future one at the moment it is written.
*(UIP-05, UIP-06)*

**2. "A row is merged, not replaced" -> two named methods with two tests, instead of one method with
two contracts.** `Rows.Merge` is asked for stable order by two callers and source order by its own
docstring. Naming the two behaviours makes the choice a call-site decision that a reader can see,
rather than an accident that depends on which comment they read. *(UIP-01, UIP-02)*

**3. "A live-app test is excluded from CI" -> a test that asserts the category, not an attribute
somebody remembers.** `LiveAppInspectionTests` lost its `[Category("LiveApp")]` in a split and
nothing noticed. A reflective test -- "every type that references a probe app or `ProbeTargetSession`
carries the category" -- is the same shape as `SecurityModelTests`'s "every advertised tool has a
security-model entry", which already works. *(UIP-14)*

**4. "Every result carries a revision and names the workspace" -> a test that calls every advertised
tool.** The rule's own wording claims it is structural ("added once, in `WorkspaceManager`, so a tool
added later cannot forget it") and it is not: three tools are spot-checked. Enumerating
`ToolNames` and asserting both fields on each result makes the claim true. *(UIP-17)*

**5. "The published layout is what the resolvers look for" -> one artefact both sides read.**
`Publish-Tree` writes a layout in PowerShell and `PublishedLayoutTests` re-types it in C#. A
`layout.json` emitted by the publish and consumed by both the package assertion and the test turns a
remembered agreement into a checked one. *(UIP-22)*

**6. "Comments carry no history and no closed-issue tags" -> a CI grep.** 100 history clauses and 60
issue tags, both up since #171 was filed. The convention is long, well argued and entirely
unenforced, and a voluntary rewrite loses to a new feature every time. Warn for one pass, fix, then
fail. Extend the file set beyond `*.cs` -- two of the violations found in this review are in
`ci.yml` and a `.csproj`. *(UIP-23, UIP-12)*

**7. "Nothing writes to stdout in stdio mode" -> the same grep step.** The first rule in CLAUDE.md,
the hardest failure to diagnose, one line to check. *(UIP-18)*

**8. "The tap's four header tiers may only include downward" -> a script over the include graph.**
`tap-tiers.md` describes a directed graph and asks a reviewer to hold it in their head. Parsing
`#include` lines out of `src/RoseMcp.Xaml.Tap/*.h` and asserting the allowed edges is twenty lines
and runs in the job that already has the C++ toolset. *(UIP-24)*

**9. "An expensive fixture is shared" -> make `TestSession.OpenAsync` the expensive path and give the
cheap one a name.** Today the cheap thing (sharing) requires knowing TUnit's `ClassDataSource` and
the expensive thing (a fresh load) is the one-liner every test reaches for. Inverting that -- a
`SharedFixture.Members` property that is trivially available, and `TestSession.OpenAsync` documented
as "for tests that mutate" -- makes the default choice the right one. *(UIP-13)*

## Open questions for Steve

1. **Was `LiveAppInspectionTests` losing its `[Category("LiveApp")]` deliberate?** If those eleven
   ICorDebug tests are meant to run on a hosted runner, that is a real gain and the CI comment needs
   rewriting to say so. If not, CI is attaching a debugger where nobody intended. (UIP-14)
2. **Is #208's hypothesis confirmed?** The code reads exactly as the issue predicts -- the host's
   bound is latency-only and the tap's UI-thread dispatch is uncancellable. Has the host log for a
   failing run been checked for the timed-out `selecthandle`? If so, the finding is a product bug
   (UIP-15) rather than a test one and should be re-labelled.
3. **`Rows.Merge`: which contract is wanted?** The breakpoint list's comment wants rows to stay put;
   the docstring promises source order. Both are defensible, but only one of them is what #221 is
   asking for.
4. **Does anything measure the live-app flake rate?** `live-app-tests.md` says green once is not
   green and that a flake rate needs more than one sample, but nothing in the repository runs the
   suite repeatedly or records the result. Is that done by hand, and is the number written anywhere?
5. **Is `WorkspaceRow` staying in the tray on purpose?** The decision record states it as a
   consequence of `InfoBarSeverity`, but never weighs it against losing five pure functions'
   testability. One enum in `Ui.Core` would end it.
6. **Is `xunit.v3.assert` on TUnit a settled choice?** It reads as migration residue and is in fact
   a good decision; without a record, somebody will "clean it up".

## Rose dogfooding notes

Used, worked, and beat the alternative:

- **`rose_find_references` on `RoseMcp.Contracts.ToolNames.LiveAppPairs`** -- to verify the
  `debugging-ui` decision's claim that the parity test guards it. Returned the definition, the
  `<see cref>` in the neighbouring docstring, and both `ToolParityTests` lines, in one call, with
  previews. Grep would have matched the same three but not told me the two test hits were in the same
  method. **Clear win, and it settled a spot-check outright.**
- **`rose_find_references` on `RoseMcp.Ui.Core.Rows.Merge`** -- to enumerate call sites for UIP-01.
  Returned five production sites plus the test. I had found three by reading; it found
  `InspectedSession.Absorb(LiveBreakpointList)` and `Absorb(LiveTracepointList)`, which I had missed
  entirely -- and those two carry the comment that contradicts the docstring, which is the most
  interesting fact in the whole finding. **The strongest single result of the review: it changed the
  finding rather than confirming it.** Grep for `Rows.Merge` would have found them too, in this case,
  because the call is spelled with the type name; the reason it won is that it also gave me
  `containingMember` for each, which is what surfaced the contradiction.
- **`rose_symbol_info` on `RoseMcp.Tray.MainWindow.DescribeHeadline`** -- to confirm accessibility and
  the declaration span for UIP-06. Answered with `"accessibility": "Public"`, `isStatic` implied by
  the signature, and `declarationSpans` giving 352-372 so I could cite the range without reading.
  Worked. Note: with `includeSource` omitted it returns `"source": []` rather than omitting the key,
  which costs a couple of tokens and reads as "no source" rather than "not asked for".
- **`rose_symbol_info` on `RoseMcp.Ui.Core.Inspector.InspectorOptions.InstanceKey`** with
  `includeSource: true` -- to check `Program.cs`'s "one inspector per machine" claim against the key
  it actually computes. Returned the full XML doc and the three-line body. **Exactly the tool for
  this**: the question was "what does this property actually do", and it answered without opening the
  file.

Used and lost:

- **`rose_outline` on `src/RoseMcp.Tray/MainWindow.xaml.cs`** -- to survey the tray's code-behind
  before reading it. It returned roughly 10,000 tokens for a 651-line file, because it merged in
  every member of the XAML-generated `MainWindow.g.i.cs` partial (23 control fields,
  `InitializeComponent`, `UnloadObject`, two generated binding interfaces, `_contentLoaded`) and gave
  each member a full `location` object with an absolute path, a preview and a `containingMember`,
  even with `includeDocumentation: false`. Reading the file with `sed -n` cost a fifth of that and
  told me more. **Two defects worth filing:**
  1. *`isGenerated` is `false` for every member declared in `obj/.../MainWindow.g.i.cs`.* The tool's
     own description says "Members a generator wrote are marked, since there is no file to edit for
     those" -- these are exactly that case and they are not marked. For a XAML code-behind, which is
     the single most common partial-class shape in a WinUI project, the outline cannot be trusted to
     say which half of the type is editable.
  2. *A code-behind outline is unusable for its stated purpose* ("use it instead of reading the file
     to find out what is in it"). The generated half is two-thirds of the output and none of it is
     what the reader asked about. A `includeGenerated: false` default, or simply honouring the
     existing `filePath` argument as a filter on *declarations* rather than only as a way to pick a
     partial, would fix it -- I passed `filePath` and still got the other file's members.
  This overlaps the review's existing note that "compact `rose_outline` is not compact (every member
  carries a full location)"; the code-behind case makes it four times worse.

Not reached for, and why:

- **`rose_diagnostics`** -- nothing was edited, so there was nothing to compile-check.
- **`rose_search_symbols`** -- every symbol in this review was reached by name or by file, and
  `rose_find_references`/`rose_symbol_info` take a name directly.
- **`rose_find_implementations`** -- reached for late, on `RoseMcp.Broker.IInspectorPresenter`, to
  check an assumption I had made from a grep of file names (that it had one implementation). It has
  two, `NoInspector` and `OperatorInspector`, both at `src/RoseMcp.Broker/InspectorPresenter.cs:39`
  and `:61`, which is what makes the tray's `TryAdd`-overriding registration
  (`src/RoseMcp.Tray/App.xaml.cs:66-75`) mean anything. **The grep had told me which files mention
  the name; only this told me what implements it.** I should have reached for it sooner -- the note
  I had written before running it was wrong, which is the most useful kind of dogfooding result.
- **Nothing rose offers answers the questions Part B and Part C are made of.** Counting
  `FixtureSolution.Copy` per test class, finding history clauses in comments, checking whether a test
  class carries an attribute, reading `.yml` and `.ps1` -- all of it is grep, and correctly so.
  Worth saying plainly: **roughly 80% of this review's evidence came from grep and `sed`, and that is
  not a defect in Rose.** Rose answers questions about C# *semantics*; a maintainability review is
  mostly questions about text, arrangement and non-C# files. The place it won was every time the
  question was "who calls this" or "what is this really", and it won decisively there.
