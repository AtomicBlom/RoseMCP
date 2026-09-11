# The inspector is a client of the broker, not a window in the tray

**Decision.** `RoseMcp.Inspector` is its own executable. It owns no debug session, no worker and no
Roslyn workspace; it reads and drives everything over an http operator API the broker serves, and it
requires a tray or an http server to be running. One window is about one debugged process.

**Why not a window in the tray.** Restarting the tray drops every loaded solution and every debug
session, and a debugger UI is a far larger crash surface than a status panel: it walks stacks, reads
memory through paths a person types, expands object graphs, and drives a native XAML provider inside
somebody else's application. A fault in any of that would take the workspaces with it. As a separate
process it can be killed and restarted while the sessions it was looking at carry on.

**Why it cannot own a session.** A process has one debugger. `LiveAppSessionManager` already tracks
every session, including the ones an agent started from a stdio client, and the useful case is
exactly the one where an agent attached and a person wants to look. An inspector that established
its own session could only ever inspect the sessions it created, which is the opposite of what it is
for.

**Why an operator API rather than the agent tools.** `CallSession.Id` is null inside a minimal-API
endpoint, so `LiveAppSessionManager.Find` -- which is owner-scoped, correctly, so one agent cannot
drive another's session -- refuses every call made from an http endpoint. The operator is not an
agent: they are the person running the broker. `ForOperator` is owner-agnostic and says so in its
own docstring, and the bearer token is what authenticates it.

**Why a token and not loopback-only.** Loopback is not a boundary on a developer machine: every
process on it, including the target being debugged, can reach a loopback port. The operator surface
reads memory out of an attached process and can move it, so "anything local" is the wrong audience.
The tray mints one per run, hands it to the inspector it launches, and offers it on a menu item for
a window started by hand. Per run rather than persisted, because a token that outlives the process
is one that has to be revoked; the cost is that an inspector left open across a tray restart is
refused, which it says in as many words.

**Why the panes are toggled rather than navigated.** A `Frame` recreates a page on every visit, and
what that loses is the reader's place: the event tail, an open expander, the scroll position, a
selected frame, an expanded value. Those are the whole state of looking at something.

## What the inspector must never do

**Hold a target it is not showing.** A stop is on a timer whichever way it was taken, because an
unattended stop wedges somebody's application. The window holds one only while a pane that reads a
stop is visible, gives it back the moment the reader looks elsewhere, and waits for the release
before it closes -- a hold that outlives the window is an app frozen for minutes with nothing on
screen to say why.

**Answer about a target it has not asked.** Every pane reads through the same session poll and says
what it could not read rather than showing an empty list: frames from a stop that has ended stay
under a banner, a tree the provider could not deliver says why, an element that has left the tree
takes its properties with it. The failure this whole surface is built against is the confident wrong
answer, not the missing one.

**Serve XAML into a held target.** The provider answers on the app's own UI thread and the debugger
is holding that thread, so the tab goes dead and says so rather than timing out. That is a limit of
the mechanism rather than a choice, and a reader is told which.

## What is deliberately absent

**A session list.** The tray lists sessions; this window is about one process and says which in its
own title. A window that could be re-pointed at another process would make that title a lie.

**Any Roslyn.** It references `RoseMcp.Ui`, `RoseMcp.Ui.Core`, `RoseMcp.Contracts` and the logging
sink, and nothing that loads a solution. The testable half of it is `RoseMcp.Ui.Core`, which is plain
`net10.0` and runs in the fast suite; what is left in the WinUI project is the part no test can see
anyway.
