# The debugging and XAML inspection UI is a separate process that reads the broker over http

**Decision.** A person inspecting a debug session gets `RoseMcp.Inspector`, a WinUI 3 app of its own,
launched from the tray and talking to the tray's in-process broker over a loopback http surface
mounted at `/operator`. The tray grows a debug-sessions section beside its workspace cards; the
inspector holds everything else -- the event tail, breakpoints, the call stack, locals, threads, and
the live visual tree.

**Why a second process rather than another tray window.** The tray *is* the broker. Restarting it
drops every loaded solution and detaches every debug session, so iterating on a tree viewer inside it
costs a full reload each time. A tree over several thousand nodes with a property grid is also a much
larger crash surface than a status panel, and an unhandled binding exception there would take the
workers with it. What the inspector may never be is a debugger of its own: a process has one
debugger, so the inspector is always a client of a session some agent started.

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

**What stays in the tray.** `WorkspaceRow`, because it reaches for `InfoBarSeverity`;
`StartupRegistration`, because an inspector does not start with Windows; and `TrayOptions`.

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

## Only UWP and WinUI targets get a XAML tab

**Decision.** The host probes the target's loaded modules for its `XamlStack` when it establishes the
session, re-probing while the answer is `Unknown`, and reports the stack with the sentence that
decided it. The inspector shows a XAML tab only for `Uwp` and `WinUi`, and otherwise puts one line in
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

## One hold, shared by the panes that read a stop

**Decision.** The hold on a stopped target belongs to a `HoldKeeper` on the window, not to a pane.
Panes say whether they want the target kept where it is, and the keeper reconciles: it takes one
hold while anybody wants one and gives it back when nobody does.

**Why.** The host has one hold, and the last request about it wins. Two panes each taking and
releasing their own is not two holds, it is a race -- the stack pane released on being hidden gave
away the hold the threads pane had just taken, and the target resumed under a reader who had done
nothing but change tab. Wanting rather than doing is also what makes it safe to call from a poll: a
pane says the same thing every second and only a change asks the host anything, and a pane that
forgets to let go is corrected by its own next poll rather than leaving somebody's application
stopped.

**Why the reconcile is deferred by a yield.** Changing tab tells the arriving pane and the leaving
one in a single pass. Acting on the first of those releases the hold and takes it again, and the
target is free in between -- which was measured against the probe as a release and a re-take per
switch in the host's own event log. Deferring to the end of the caller's block collapses the pass
into one decision whatever order the panes are told in, so no call site has to remember an ordering.

**Why a pane claims on becoming visible rather than on its next poll.** Claiming from the poll is a
second of nothing wanted, and the leaving pane's release goes out inside it. Both halves have to
happen in the same block or the deferral has nothing to collapse.

## A hold outlives the window unless the close waits for it

**Decision.** Closing the inspector cancels the close once, gives the hold back, waits up to two
seconds, then closes for real.

**Why.** Releasing on `Closed` and disposing the operator client in the same handler cancels the
request that was just made, so the target stays stopped until the host's own cap expires -- minutes
of somebody's application frozen because a window was closed, with nothing left on screen to say so.
It is budgeted because a window that will not close is worse than a target that frees itself in a
few minutes: a tray that has stopped answering must not take the inspector with it.

## Threads are re-read when the stop moves, not on a timer

**Decision.** The threads pane reads when the stop's sequence changes while it is visible, rather
than polling every two seconds.

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
window takes the hold the moment the pause lands -- otherwise pausing from the events tab is a target
that starts again half a minute later, which reads as the button not having worked. The hold is told
which stop it is for before it is asked for: the keeper drops what it believes it holds when the stop
moves, so taking one for a stop it has not heard of yet is a hold taken and then immediately taken
again.

**A stop taken and not kept goes back.** A breakpoint can arrive in the moment between asking and
the stop taking effect, and it owns the stop it made. Ours is then a second stop on the same process,
which one continue would not undo -- so it is given back and the caller is told about the stop that is
really there. Same for a target with no managed thread to report the pause on.

## What moves the target lives above the tabs

**Decision.** Pause, Continue and the three steps are a bar over the whole window, not a toolbar
inside the stack pane.

**Why.** Moving a target is something you do to the session, not to a stack. Buried in one pane they
were only reachable from the tab that is least useful while the target is running -- so wanting to
pause while watching the event tail, or to continue after reading the visual tree, meant changing tab
to press a button and changing back. The stack pane keeps only what describes a stop.

**Why exactly one half is ever live.** Pause is for a running target and the rest are for a stopped
one, and everything goes dead while a request is in flight: a second press is a step issued into a
target that is already being moved.
