# The inspector is a client of the broker, not a window in the tray

**Decision.** An Inspector window is a window of the RoseMCP app, `RoseMcp.App`, its own executable (see
[the decision](one-rosemcp-app-per-broker-holds-every-window.md)). It holds no debugger, no worker and no
Roslyn workspace; it reads and drives everything over an http operator API the broker serves, and it
requires a tray or an http server to be running. One window is about one debugged process. A person can
start a session from it -- attach to a running app, or launch an installed app or a program -- and the
broker's session manager starts and holds that session, exactly as it does an agent's.

**Why not a window in the tray.** Restarting the tray drops every loaded solution and every debug
session, and a debugger UI is a far larger crash surface than a status panel: it walks stacks, reads
memory through paths a person types, expands object graphs, and drives a native XAML provider inside
somebody else's application. A fault in any of that would take the workspaces with it. As a separate
process it can be killed and restarted while the sessions it was looking at carry on.

**Why the window holds no debugger of its own.** A process has one debugger. `LiveAppSessionManager`
already tracks every session, including the ones an agent started from a stdio client, and the useful
case is often the one where an agent attached and a person wants to look. A window that attached
ICorDebug itself could only ever inspect the sessions it created, could not hand one to an agent, and
would take its debugger with it when it closed.

**Why a person can start a session anyway.** Debugging without an agent is a real case -- an app
misbehaving in front of you, with no conversation open -- and the only alternative was asking an agent
to attach so a window could look. So the start is a request to the broker (`/operator/debug/attach`,
`/operator/debug/launch`), which starts the session through the same `AttachAsync`, `LaunchAsync` and
`LaunchPackageAsync` the agent tools call. The attach rules, the refusal of the broker's own process and
its hosts, the answer for a process already being debugged and the reclaim of a start that faulted or
whose asker gave up are therefore one implementation, and a person's session is supervised, listed and
dropped like any other. The window shows a start page while it is about nothing: the running .NET apps
with a window (each being debugged opening its session instead, since a process has one debugger), a
launch form, and what was debugged lately.

**Why a person's session is theirs until shared, and shared with one session.** A session's owners are
a set (`LiveAppOwners`): an agent's session belongs to the MCP session that started it, a person's to
the person, and the person may add one agent session -- by its row in the client table, matched on the
key the table files it under, the relay's name for its session. The person is a party no tool call is,
so a session started from a window is reached by no agent until shared: not by a stdio broker's null
session, and not by the one unnamed http client, both of which an agent's own session admits. Sharing
with the unnamed client is refused for the reason a note to it is: it could be any client without a
relay. One client rather than several, because the question a share answers is "ask this agent about
what I see", and every further reader is a further thing that can move the target.

**Why an agent leaving a shared session does not end it.** An agent tidying up calls
`rose_debug_detach`, and doing so on a session the person started would take the person's debugger
away mid-look. So no tool call ends a session a person started: the agent's detach ends its share and
says so, and the debugger stays attached for the person.

**Why an operator API rather than the agent tools.** `CallSession.Id` is null inside a minimal-API
endpoint, so `LiveAppSessionManager.Find` -- which is owner-scoped, correctly, so one agent cannot
drive another's session -- refuses every call made from an http endpoint. The operator is not an
agent: they are the person running the broker. `ForOperator` is owner-agnostic and says so in its
own docstring, and the bearer token is what authenticates it.

**Why a token and not loopback-only.** Loopback is not a boundary on a developer machine: every
process on it, including the target being debugged, can reach a loopback port. The operator surface
reads memory out of an attached process and can move it, so "anything local" is the wrong audience.
The tray mints one per run and hands it to the app it launches; a person who wants to start the app by
hand copies the command, token included, from the RoseMCP window's settings. Per run rather than
persisted, because a token that outlives the process is one that has to be revoked; the cost is that a
window left open across a tray restart is refused, which it says in as many words, until the tray's
next launch hands the running app the new run's token.

**Why the views are toggled rather than navigated.** A `Frame` recreates a page on every visit, and
what that loses is the reader's place: the session stream, an open expander, the scroll position, a
selected frame, an expanded value. Those are the whole state of looking at something.

## What the inspector must never do

**Hold a target it is not showing.** A stop is on a timer whichever way it was taken, because an
unattended stop wedges somebody's application. The window holds one only when the person asks -- Hold,
beside the countdown it cancels, or Pause -- never because a view that reads a stop is on screen,
since the code view always is; it gives the hold back when they continue or release it, and waits for
the release before it closes -- a hold that outlives the window is an app frozen for minutes with nothing on
screen to say why.

**Answer about a target it has not asked.** Every view reads through the same session poll and says
what it could not read rather than showing an empty list: frames from a stop that has ended stay
under a banner, a tree the provider could not deliver says why, an element that has left the tree
takes its properties with it. The failure this whole surface is built against is the confident wrong
answer, not the missing one.

**Serve XAML into a held target.** The provider answers on the app's own UI thread and the debugger
is holding that thread, so the XAML view goes dead and says so rather than timing out. That is a limit of
the mechanism rather than a choice, and a reader is told which.

## What is deliberately absent

**A session list.** The RoseMCP window's Debugging view lists sessions; this window is about one process and says which in its
own title. A window that could be re-pointed at another process while it shows one would make that title a lie.
The start page lists running apps, not sessions, and a window leaves it only to become about the process
picked there. When its session ends it stays on it, saying the app exited and naming its log, until the
person goes back to the start page or attaches again -- a session they detached here goes straight back --
still answering for that process, so detaching and attaching again reaches the same window.

**Adopting whichever session there is.** A window opened with no session could settle on the one
session running, and refuse to guess between several. It asks instead: the start page is where a person
says which process they meant, so no window is ever about a program it was not pointed at, and one
opened while nothing is being debugged has something to do rather than an empty state.

**Any Roslyn.** It references `RoseMcp.Ui`, `RoseMcp.Ui.Core`, `RoseMcp.Contracts` and the logging
sink, and nothing that loads a solution. The testable half of it is `RoseMcp.Ui.Core`, which is plain
`net10.0` and runs in the fast suite; what is left in the WinUI project is the part no test can see
anyway.
