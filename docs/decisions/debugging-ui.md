# The debugging and XAML inspection UI is a separate process that reads the broker over http

**Decision.** A person inspecting a debug session gets an Inspector window of `RoseMcp.App`, a WinUI 3 app of its own,
launched from the tray and talking to the tray's in-process broker over a loopback http surface
mounted at `/operator`. The tray has no window of its own (see
[the decision](the-tray-process-owns-only-its-icon.md)), and the app holds the RoseMCP window beside it
([the decision](one-rosemcp-app-per-broker-holds-every-window.md)); the Inspector holds everything -- the event
tail, breakpoints, the call stack, locals, threads, and the live visual tree.

**Why a second process rather than another tray window.** The tray *is* the broker. Restarting it
drops every loaded solution and detaches every debug session, so iterating on a tree viewer inside it
costs a full reload each time. A tree over several thousand nodes with a property grid is also a much
larger crash surface than a status panel, and an unhandled binding exception there would take the
workers with it. What the inspector may never be is a debugger of its own: a process has one
debugger, so the inspector is always a client of a session the broker holds -- one an agent started,
or one the person started from the Inspector's start page, which asks the broker to attach or launch
rather than attaching anything itself (see
[the decision](the-inspector-is-a-client-of-the-broker.md)).

**Why it requires the tray.** A stdio broker has no listener to connect to, and its session lives and
dies with one client. The inspector says so plainly when no tray answers rather than starting
anything itself.

## The operator surface is owner-agnostic and holds a token

**Decision.** `/operator/*` resolves sessions through a new `LiveAppSessionManager.ForOperator`, which
skips the ownership check `Find` applies, and every request must carry a bearer token the tray mints
per run. The tray passes it to the inspector it launches and offers it as a copyable command line.

**Why not reuse `Find`.** `CallSession.Id` comes from a call-tool filter reading the MCP transport's
session id, so inside an ASP.NET endpoint it is null. `Find` compares that null against the owner
recorded when the session started, so an operator endpoint would be refused for every session an
agent started -- which is all of them.

**Why a token rather than the loopback and Origin checks alone.** A session id is eight hex
characters and the ownership check exists precisely because guessing one reaches somebody else's
debugger. An endpoint that bypasses that check reintroduces the hole for any process running as this
user, so it gets a secret that agents never see. Minted per run rather than persisted: an inspector
holding yesterday's token should be told to relaunch from the tray, not silently authorised.

**The token is also all that stands between a request and starting a debugger.** `/operator/debug`
attaches to any of this user's processes and launches any program a request names, as this user, so
it sits behind the same token, keeps every rule the agent's attach keeps -- being the same attach --
and refuses the broker's own process and its hosts. A program is named by its full path, an `.exe`
that exists, since a window has no folder to measure a relative path from. None of it is offered on
the MCP surface: a session these start is the person's, and an agent able to start one would be making
sessions in the person's name that no tool call could then end. The running
apps and installed packages it lists are read broker-side behind an interface (`IRunningApps`,
`IInstalledApps`), so the routes run in the unit suite against a desktop a test describes.

## Execution state is orthogonal to session lifecycle

**Decision.** `LiveAppSessionState` keeps meaning lifecycle -- `Starting`, `Ready`, `Faulted`,
`Ended`. A new `LiveExecutionState` (`Running`, `StoppedAtBreakpoint`, `StoppedAtStep`) sits beside
it, with a `LiveStop` carrying the thread, the breakpoint, the stop's own event sequence, and who
will let the target go (`LiveStopResume.AutoContinue` or `HeldByOperator`) with a deadline.

**Why the stop's event sequence is on it.** It is the stop's identity. A UI that re-reads frames
needs to know it is looking at a *new* stop rather than the same one polled again, and the sequence
of the `BreakpointHit` or `StepComplete` event that announced it is the only number both ends already
agree on.

**Why the hold is a resume mode rather than a fourth execution state.** A held target is still
stopped at a breakpoint or a step, and the pill a reader wants says which. Folding the hold into the
state loses the cause; putting it on `Resume` keeps both and makes the deadline mean one thing.

## What the inspector reports about XAML is the provider's residency, not a channel

**Decision.** The session reports `LiveXamlProvider` -- `None`, `Resident` or `Lost`.

**Why not a channel.** A channel is what served one read, and there is only one channel now: the
provider connects back on a named pipe and every request is a message on it. `LiveXamlTree.Channel`
still answers "which channel served this read" for a caller who wants to see the fast path is there.
What a status view needs is different and cheaper to act on: whether a provider is resident, so a
read is a message, or absent, so the next read pays an injection. `Lost` is a real third state rather
than a tidy-up -- a pipe that drops sends the next call back through injection, which the host already
warns about, and a session doing it repeatedly is a channel failing quietly.

## The shared UI is two projects, split by what a test can reach

**Decision.** `RoseMcp.Ui.Core` is plain `net10.0` and holds everything testable: the rows a window
binds to, the formatting they carry, the merge that keeps them alive across a refresh, and the poll
loop. `RoseMcp.Ui` is `net10.0-windows` with `UseWinUI` and holds the resource dictionaries, the
window chrome and the crash handler. Both apps reference both.

**Why not one project.** A `net10.0` test project cannot reference a `net10.0-windows` assembly at
all, so anything in the WinUI half is code no unit test can see. This repository has already settled
that question twice, in the same direction: `XamlStackModules` lives in Contracts and markup parsing
lives in `RoseMcp.XamlDiff`, both because the host that owns the behaviour cannot be referenced from
a test. The split is what makes the rows and the formatting testable, and the 489-test unit suite
covers them on Linux as a result.

**What stays in the tray.** `StartupRegistration`, because an inspector does not start with Windows;
`TrayIconArt`, because it draws with `System.Drawing`; and `TrayOptions`. What the icon says is in
`RoseMcp.Ui.Core`, for the same reason as the rows.

## A referenced project's assets land under its project name, and the library owns that path

**Decision.** The icon lives once, in `RoseMcp.Ui/Assets/`, and `RoseUiAssets` is the single place
that spells where it ends up at runtime: `RoseMcp.Ui/Assets/` beside the consuming app's exe, not
`Assets/`.

**Why not what it looks like.** WinUI copies a referenced project's `Content` into each referencing
app's output under a folder named for the project. Neither `Link` nor `TargetPath` on the app's own
item moves it, which cost three build cycles to establish. Two ways out were rejected: duplicating
the files per app makes the pixels exist twice for no gain, and letting each app spell the path
encodes the surprise once per app. The chosen shape has the same structure as the `ms-appx` URI the
XAML already uses, so both halves agree.

**One thing that follows.** A shared resource dictionary is merged as
`ms-appx:///RoseMcp.Ui/Themes/Rose.xaml`, and it must be merged *after* `XamlControlsResources`,
because every style in it is `BasedOn` a stock WinUI one and that resolves at parse time up the merge
chain. Getting the order wrong fails to parse, and the only symptom is the process disappearing
during the `App` constructor -- which is why the crash handler landed in the same change.

## Only UWP and WinUI targets get a XAML view

**Decision.** The host probes the target's loaded modules for its `XamlStack` when it establishes the
session, re-probing while the answer is `Unknown`, and reports the stack with the sentence that
decided it. The inspector offers a XAML view only for `Uwp` and `WinUi`, and otherwise puts one line in
the header naming what the target is and why it cannot be inspected.

**Why probe at establish rather than at the first XAML call.** The probe is a module-list read
costing microseconds and needing nothing but a pid, and until now it only ran inside the first
injection -- so a status view could not say whether a target had XAML without paying for an injection
to find out. Re-probing while `Unknown` matters because frameworks load late: a target attached at
startup has not loaded `Windows.UI.Xaml.dll` yet, and a permanent `Unknown` from one early look would
be a wrong answer rather than an unknown one.

## Local names and lines come from the portable PDB, and a stale one is refused

**Decision.** `RoseMcp.Symbols` reads a module's metadata and its portable PDB. A stopped frame's
locals carry the names the source declares, resolved for the scopes covering that frame's own IL
offset, and an IL offset resolves to a file and a line. A module with no symbols leaves locals as
`local_0` upwards by slot, which is what they all were before.

**Why the IL offset is not optional.** A slot is reused by locals in sibling blocks, so which name a
slot has depends on where execution is. Naming every slot from every scope in the method at once
hands back two names for one slot and picks between them arbitrarily -- the shape of failure this
whole reader exists to remove, since the caller cannot tell a wrong name from a right one.

**Why a mismatched PDB is refused rather than read.** A PDB left over from an earlier build reads
perfectly well and answers with names and line numbers out of code that is not running: confident,
plausible, and wrong. `PEReader.TryOpenAssociatedPortablePdb` checks the PDB's id against the
module's debug directory, which is why symbols are opened through it rather than by opening the file
next door. `PdbState` then separates "no symbols" from "somebody else's symbols", because the two
send a reader to different places, and a null would say only that something was unknown.

**Why a hidden sequence point is an answer.** A hidden point says the IL from here maps to no source
at all -- compiler prologue, an `await`'s state-machine bookkeeping, an iterator's closing machinery.
So it stops the search rather than being skipped: skipping to the last real point before it puts a
frame on a line whose code is not executing.

**Why the module file is prefetched and closed.** This runs in a process that lives for hours beside
a developer who is rebuilding. A held handle on their output fails their next build with MSB3021,
which is the same failure the analyzer loader shadow-copies to avoid.

**Why the evaluator matches the name it reported.** `rose_debug_evaluate` resolves a root against the
same names the stop's recorded frame reported, so a caller passes back what they were shown. A slot
number still resolves, since a module without symbols reports slots and a caller reading an older
event may hold one.

**Why the tests compile their own module.** Read against the test assembly's own PDB, what these
tests assert would depend on how the suite was built: optimised code loses a local to a stack temp
and merges the scope it was declared in, and CI runs the unit suite in Release as well as Debug. So
`CompiledModule` emits a small assembly at `OptimizationLevel.Debug` with the source beside the
assertions about its lines, and the fixture the live-app test stops on carries `NoOptimization` for
the same reason.
## Stop inspection answers with a state, never a refusal

**Decision.** Frames, a frame's variables, an expanded value, the thread list and the hold all return
a `LiveExecutionReport`: an execution state, the stop they were read at, and a `Detail` sentence.
Asking any of them while the target is running answers `Running` with an empty result and the reason,
rather than throwing.

**Why not refuse.** A stop ends for reasons the caller did not cause -- the safety timer, a hold
expiring, an agent continuing -- so a reader that polls a stop will hit the running case routinely.
An error there reads as a broken call and sends someone to the logs; a state reads as what it is.
Bad *arguments* still throw, because a frame index past the end of a stack is the caller's mistake
and the two must not be confused.

**Why every report echoes the stop.** Everything a debugger hands out is valid only within the stop
it came from -- `CorDebugValue` and `CorDebugFrame` are invalidated the moment the target moves. A
reader holding frames from one stop and variables from the next has a view that never existed, and
`LiveStop.EventSequence` is the only thing that makes that detectable.

## A value is addressed by slot, and the grammar is shared

**Decision.** `ValuePath` -- `arg:0`, `local:2` or a bare name, then `.field` and `[3]` -- lives in
`RoseMcp.Contracts` beside `LiveVariable.Path`, which is the only thing that produces one. Every
variable the debugger reports carries the path that expands it, and `rose_debug_evaluate` resolves
the same grammar through the same resolver as an expansion.

**Why a slot rather than a name.** A name is not always unique or even present: a compiler temporary
has none, and two sibling blocks reuse one slot under different names. The slot is what the runtime
addresses, so it always works, and a caller that has a name can still use it.

**Why in Contracts.** It is a pure function over strings, which is the exception that already put
`XamlStackModules` and `ToolArgumentShape` there: the host that owns the behaviour is
`net10.0-windows`, so a grammar living beside it is a grammar no test can see. Resolving a parsed
path against live `CorDebugValue`s stays in the host, where it cannot be tested without a target.
The rule that keeps this honest is unchanged -- nothing holding state, touching Roslyn, or knowing
what a tool does goes in Contracts.

**One consequence worth stating.** Because an expansion lists the fields of the exact type *and every
base above it*, resolving a path had to walk the same chain. It used to ask the value's own class
only, so a base class's field could be listed by an expansion and then fail to resolve when the
caller passed its path back -- the two surfaces disagreeing about what an object holds.

## Stop inspection is not offered to agents

**Decision.** The five inspection tools are host-internal and reached only through `/operator`. They
are deliberately absent from `ToolNames.LiveAppPairs`, so the parity test fails if anyone adds one
there without a broker tool to match.

**Why.** An agent asks a question and reads one answer, which the stop event's captured frame and
`rose_debug_evaluate` already serve. A person scrolls a stack, opens a tree, and needs the target to
stay still while they do it -- which means the hold. A hold an agent forgets to release is somebody's
application frozen for up to ten minutes, and an agent has no way to notice it has walked away.

**What the hold costs the agent surface, and why it is loud.** An agent's `rose_debug_continue`
during a hold succeeds and releases it, rather than being refused: a continue means continue. But a
hold that vanishes silently is a reader's stack disappearing with nothing to explain it, so the
resume says so in `LiveContinueResult.Detail` and in the event stream, and the XAML refusal names the
hold when one is in place -- because the usual advice, wait for the auto-continue timer, is wrong
while a hold is suspending it.

## A stack says what it could not represent

**Decision.** A frame carries `SkippedBefore`, the count of native, internal or dynamic frames
immediately below it that the runtime gives no IL frame for, plus `Mapping` (how well the instruction
pointer maps to IL) and `Symbols` (whether the module had symbols, and if not, why).

**Why.** Dropping unrepresentable frames silently turns a stack with a native transition in it into a
complete-looking stack with a surprising caller, which is the same shape of confident wrong answer as
a stale PDB. `Mapping` matters for the same reason: every value but `Exact` means the line beside it
is an approximation, and optimised code maps approximately as a matter of course.
## A breakpoint is chosen by reading the method, not by spelling its name

**Decision.** The panel searches the target's loaded modules by name, shows the chosen method's
source, and sets the breakpoint on the line somebody clicks.

**Why.** An agentic session has no IDE open beside it, so the old text box asked a person to remember
a namespace, a type and a method exactly, and a location that is one character out does not bind. The
search is over the modules the target has actually loaded, which is also the honest scope: a method
in code the target has not reached yet is not there to find, and the panel says so rather than
reporting an empty result as a spelling mistake.

**Why it reads files and not the debuggee.** Which modules are loaded is the only thing the target is
asked, once, in a balanced stop and continue that leaves a held target exactly as held as it was.
Everything after that is metadata and source on disk. So a search answers between keystrokes while
the app runs, and answers while the app is wedged -- which is when somebody most wants a breakpoint.

## A position names the method its instructions are in, which is not always the one on screen

**Decision.** The positions offered for a method cover the lambdas, local functions and state
machines written inside it, and each carries the location that breaks there. A line with places to
stop in more than one method is more than one row, and a row whose method is not the one being read
says which method it is.

**Why.** A line inside a lambda compiles into a method of the compiler's own, and a line after an
`await` into a state machine's `MoveNext`. Those are exactly the lines somebody most wants to break
on, and a picker that offered only the named method's own sequence points would refuse them while
appearing to work. Picking one of several silently is worse still: the breakpoint lands in code
nobody pointed at, and the only symptom is the target stopping somewhere unexpected.

**What that cost.** An `async` method has no sequence points at all. A region seeded on the named
method's own extent therefore finds nothing for it, and the emptiness reads as a method with no code
in it -- so the state machines the PDB links to a method are a seed and not merely a step.

## The instruction offset rides in the location

**Decision.** A location may end in `@IL_001F`, and that is how a picked position is carried, rather
than as an argument of its own.

**Why.** What a breakpoint reports is then what sets the same breakpoint again. It also keeps the
agent-facing and host-facing tools declaring the same arguments, which the parity test checks, and
the capability reaches an agent for free rather than being a second grammar nobody documented.

**Why it is strict.** The marker is anchored past the last dot, so something spelled like it inside a
generated type's name is part of the name. A marker that is there and unreadable is refused rather
than dropped, because falling back to the method's first instruction is a stop in the wrong place
reported as a success, and nothing downstream could tell the difference. `SymbolLocation` moved to
`Contracts` to be tested at all, for the reason `ValuePath` is there.

## Where a breakpoint bound is reported, and overloads are not disambiguated

**Decision.** A breakpoint and a tracepoint each carry the file and line they actually bound at. A
location naming a method with several overloads still binds to whichever metadata lists first.

**Why.** A location names a method, and two overloads share one, so the grammar cannot express which
was meant. Reporting where it landed is what lets somebody see it went somewhere other than where
they meant -- which is the whole of the problem, since an overload breakpoint that binds to the wrong
one is otherwise indistinguishable from a working one. Making the grammar carry a parameter list is
the real fix and is a change to every layer that parses a location.

## Missing source is a sentence, not a refusal

**Decision.** No such module, no symbols, symbols from another build, or a source file this machine
never had: each is a sentence and an empty listing, and the method's first instruction stays
available to break at.

**Why.** A PDB records the path of whichever machine compiled the module, so anything out of a
package or off a build agent names a directory that was never here. That is the ordinary state of
most of what a target loads rather than a fault, and hiding those methods from the search would mean
the one thing a name-addressed breakpoint has always been able to do -- stop at a method's entry --
stopped being offered for them.

## One hold, shared by what asks for it, and taken only when a person asks

**Decision.** The hold on a stopped target belongs to a `HoldKeeper` on the window. What wants the
target kept where it is says so, and the keeper reconciles: it takes one hold while anybody wants one
and gives it back when nobody does. Only a person asks: Hold, beside the countdown it cancels, and
Pause, since somebody who pressed Pause is by definition present. Being on screen is not asking.

**Why a view on screen does not hold.** The code view is always on screen in the one-view layout, so
a hold taken for it would keep every agent's stop for as long as the window stayed open -- the safety
timer a stopping breakpoint carries exists so that an unattended stop cannot wedge somebody's
application, and a window left open is the commonest unattended thing there is. Reading a stop takes
well under a second, and a stack the timer runs off is kept under a banner saying so.

**Why one keeper.** The host has one hold, and the last request about it wins. Two holders each
taking and releasing their own is not two holds, it is a race -- one letting go gives away the hold
the other has just taken. Wanting rather than doing is also what makes it safe to call from a poll: a
holder says the same thing every second and only a change asks the host anything, and one that
forgets to let go is corrected by its own next poll rather than leaving somebody's application
stopped.

**Why the reconcile is deferred by a yield.** Two holders told in one pass -- a pause landing while
Hold is released -- would otherwise release the hold and take it again, and the target is free in
between. Deferring to the end of the caller's block collapses the pass into one decision whatever
order the holders speak in, so no call site has to remember an ordering.

## A hold outlives the window unless the close waits for it

**Decision.** Closing the inspector cancels the close once, gives the hold back, waits up to two
seconds, then closes for real.

**Why.** Releasing on `Closed` and disposing the operator client in the same handler cancels the
request that was just made, so the target stays stopped until the host's own cap expires -- minutes
of somebody's application frozen because a window was closed, with nothing left on screen to say so.
It is budgeted because a window that will not close is worse than a target that frees itself in a
few minutes: a tray that has stopped answering must not take the inspector with it.

## Threads are re-read when the stop moves, not on a timer

**Decision.** The code view reads the threads, the stopped thread's stack and the top frame's values
when the stop's sequence changes, and another thread's stack, or another frame's values, when the
person chooses it -- never on a timer. Any thread's stack can be read, not only the stopped one's: a
deadlock is a shape across threads, and the thread holding the lock is rarely the one a breakpoint
caught. An answer for a stop that has moved on, or for a thread no longer chosen, is dropped.

**Why.** A stopped target's threads cannot go anywhere -- that is the same fact that makes the list
answerable at all, since enumerating threads needs the runtime synchronized. A poll would spend a
request a second on an answer that cannot have changed, at an application somebody is holding still.
The session poll already carries the stop's sequence, so a new stop is noticed within a second
either way.

## A visual tree is merged into the rows already on screen, never rebuilt

**Decision.** A read of the tree brings the existing rows in line with it: a row is kept while its
handle is still there, an element that has moved between parents is the same row either side, and a
subtree that has gone takes the selection with it.

**Why.** A handle is stable for the life of the element, so a row can outlive a read -- and the read
that matters most is the one after a live edit, which is exactly when somebody is watching one
element. Rebuilding closes every expander, drops the selection, and loses the scroll position, which
is a reader's whole place in a tree of several hundred elements. It also makes the one thing this
pane must never do easy: showing the properties of an element that is not in the tree any more. The
selection is cleared and said rather than left standing.

**Why an unplaceable element goes to the top rather than being dropped.** A handle listed twice, a
parent nobody listed, and a parent that is its own descendant are all snapshots that do not quite
describe a tree. Each has the same wrong answer available -- drop the element -- and taking it hands
back a tree that reads as complete and is missing a subtree, which is the one failure a reader
cannot spot. They are shown at the top and flagged.

## Properties are grouped by what set them

**Decision.** Local, then Style, then Inherited, then Animation, then anything else, then the
framework's defaults, and alphabetical inside each. The framework's dozen provenances collapse into
those five buckets, matched whole.

**Why.** Provenance is what a reader acts on: the same value means different things set on the
element, inherited from a parent, or handed over by a style nobody looking at the markup would think
to open. Sorted by name alone, the two properties somebody actually set are buried among ninety
defaults -- which is the state `includeDefaults` is turned on to escape and then regretted. Matched
whole because a substring match files `DefaultStyle`, which is a style, under Default: a confident
wrong answer about which file to go and edit. A provenance this does not know is shown as itself
rather than guessed at.

## The tree control deals in nodes at both ends, and says so nowhere

**Decision.** The pane reads a click through `NodeFromContainer` and writes a selection through
`SelectedNode`, never through `SelectedItem`.

**Why.** `TreeView` in ItemsSource mode is documented to surface the data item. It surfaces the
`TreeViewNode` wrapping it -- on a click, and as what the selection setter will accept. Both halves
fail silently: a cast that did not match reads exactly like a click on empty space, and an item
handed to the setter is ignored. Between them that is a pane where clicking a row highlights it and
shows nothing, and where a pick in the app reads the right element and highlights no row. A node
exists only once the control has built the row's container, so a reveal opens the ancestors and
then waits a bounded handful of frames for one rather than selecting immediately.

## The app is the truth about selection, and about select mode

**Decision.** Armed, Just my XAML and what is selected are read from the app twice a second, and the
toolbar follows what comes back rather than what was asked for.

**Why.** The person can arm, pick and cancel from the app's own toolbar without this window being
told, so what this side last requested proves nothing. The cost is that this window's own push comes
back looking exactly like somebody's click, which would reveal and re-select the same element once a
second forever -- so the handle last pushed is remembered and passed over. A toggle is put back to
what the app said for the same reason: one showing a mode the app is not in is worse than one that
visibly did not move.

**Why the toolbar goes dead during a read.** Every call here queues on one gate, because the
provider serves one request at a time on the app's own UI thread. A button clicked during a
thirty-second tree read does nothing visible and then snaps back, which reads as a button that does
not work.

## A manual pause is its own state, not a step with nothing behind it

**Decision.** Stopping a running target where it stands is `LiveExecutionState.PausedByOperator` and
a `Paused` event, beside the breakpoint and step states rather than folded into either. The stop it
makes is the same shape as a breakpoint's -- stack, top-frame variables, a safety timer -- and every
reader of a stop is unchanged.

**Why.** How a target came to be stopped is the first thing a reader wants, because it decides
whether what they are looking at is surprising: a stack that looks like nothing in particular is
expected when you pressed Pause and alarming at a breakpoint. It was available as a step with no
breakpoint owning it, which is exactly the null the rest of this surface refuses -- and the session
would have said "a step" about something nobody stepped. The kind also carries it into the event
tail, where "paused" among the exceptions is what explains the gap in them.

**Why the stop it makes is ordinary.** Everything downstream reads a stop rather than reasoning
about what made one, so a pause that produced a differently shaped one would need every pane taught
about it. It is on the safety timer for the same reason a breakpoint is: a pause nobody comes back
to must not leave somebody's application frozen.

**Why the window holds it anyway.** Somebody who presses Pause is by definition present, so the
window takes the hold the moment the pause lands -- otherwise pausing while reading the stream is a target
that starts again half a minute later, which reads as the button not having worked. The hold is told
which stop it is for before it is asked for: the keeper drops what it believes it holds when the stop
moves, so taking one for a stop it has not heard of yet is a hold taken and then immediately taken
again.

**A stop taken and not kept goes back.** A breakpoint can arrive in the moment between asking and
the stop taking effect, and it owns the stop it made. Ours is then a second stop on the same process,
which one continue would not undo -- so it is given back and the caller is told about the stop that is
really there. Same for a target with no managed thread to report the pause on.

## What moves the target lives in the header

**Decision.** Continue, Pause, the three steps and Detach sit in the header's stop line, beside why
the app stopped, in the standard debugger glyphs with each name and its usual shortcut in a tooltip:
Continue F5, Pause Ctrl+Alt+Break, Step into F11, Step over F10, Step out Shift+F11. Detach is the
unplug. Hold stays a word, next to the countdown it cancels.

**Why.** Moving a target is something you do to the session, not to a stack, and wanting to continue
while reading the stream or the visual tree is the ordinary case. The glyphs and shortcuts are the
ones every debugger uses, so a person's hands already know them. No standard glyph means "keep it
stopped until I continue", and an icon would be read as Pause.

**Why exactly one half is ever live.** Pause is for a running target and the rest are for a stopped
one, and everything goes dead while a request is in flight: a second press is a step issued into a
target that is already being moved.

## The app, the agent and you in one view

**Decision.** The Inspector has no tabs of panes. A header says which app, its process, its framework
and the solution its agent works in; who is in the session; whether the host is answering ("Live", or
how long ago it last did once it has gone quiet five seconds); and why the app stopped, who stopped it
and when it lets go, with what moves it. Below it the code view -- threads and any thread's stack on
the left, the chosen frame's source, locals and watches in the middle -- or the XAML or Breakpoints
view, and beside whichever is shown a single stream: the app's events, the agents' calls into this
session from the activity record, and the person's notes with the answers to them, interleaved by time
and filtered to All, Agent, Stops, Exceptions, Log or Notes.

**Why one stream.** The question a person brings is what the agent did to the app and what the app did
about it -- the breakpoint an agent set, the stop it caused, the value it read. Three lists answer it
only for somebody who lines the timestamps up by hand. The filter is applied to what was collected
rather than by the host as events arrive, so changing it shows the past too.

**Why the solution is said as where the agent works.** Nothing in a debug session names a solution:
the source shown comes from the app's own symbols. The solution the agent session in the session last
called Rose about is the honest version of the mock-up's "code from", and the header's tooltip says so;
a session nobody's agent is in names none.

**When the app exits.** The window stays on it: the header says it exited, when (from the event the
host recorded), and names the session's log, and every read stops -- a session that has ended has
nothing more to say. The person goes to the start page from there, or attaches to the app again and the
same window takes the new session. A session the person detached in this window goes straight to the
start page, which is what detaching asked for.

## Who stopped it is recorded as a breakpoint is set

**Decision.** The broker records who set each breakpoint -- the person, through the operator API, or
the agent session by the key the client table files it under -- against the id the host gave it, and
the operator's view of a session names the setter of the breakpoint it is stopped at
(`LiveAppSessionSummary.StopSetBy`), and the agent session that started it (`StartedByAgent`). A
pause is the person's, since only a window can pause. A step names nobody.

**Why at set time, in the broker.** The host never learns who asked for a breakpoint, and the broker
is the one place every set passes through with the caller known: `rose_debug_set_breakpoint` names the
call's session, the operator route is the person. Host ids are not reused within a session, so the
record stays true for its life. A step is not recorded because who stepped is already in the stream as
the call before the stop; guessing it for the header would be a confident answer from inference.

## A watch is the window's, and blank between stops

**Decision.** Watches are value paths in the grammar a local's own path is written in, kept per window
and nowhere else, read at whichever thread and frame the person is looking at, at every stop. Every
value goes blank the moment the target moves, and a read answered after the person moved to another
frame, thread or stop is dropped.

**Why.** A value read at one stop is a fact about that stop; shown after it, it is a confident answer
about somewhere the target no longer is. A list kept between apps would be paths that name nothing in
the next one. A path that is not a value path is refused as it is added, since the host would refuse
it at every stop.

## Anything a person can select can be asked about

**Decision.** A thread, a frame, a local or argument, a watch, an event or exception, a call, a
breakpoint and a tracepoint each make a `NoteSubject` an agent can look up -- the session with the
thread, frame and value path, the event's sequence (`DebugEvent`), or the breakpoint's id
(`Breakpoint`) -- with a line saying it in words. The window's one composer offers the agent sessions
in the debug session: the one that started it, or the one a person shared theirs with. A row's own
menu asks about it, and Ctrl+Shift+A asks about the row selected last. A row from a stop that is over
cannot be asked about, because what it names is gone.

**Why it says the note arrives with the agent's next call.** That is how a note travels (see
[the decision](a-note-to-an-agent-rides-on-its-sessions-next-answer.md)); nothing pushes one sooner,
so the composer does not promise "now".
