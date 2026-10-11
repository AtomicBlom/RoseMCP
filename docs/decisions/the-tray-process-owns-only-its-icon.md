# The tray process owns only its icon

**Decision.** `RoseMcp.Tray` hosts the broker, one notification icon with one menu, and the
notifications that icon raises. It has no window. Opening RoseMCP -- the menu item, a click on the
icon, a notification's action -- starts the RoseMCP app in a process of its own, which reads this
broker over the operator API. Nothing opens at start-up, at sign-in or after a deploy.

**Why no window in the broker's process.** The tray owns every warm worker on the machine. A window
is the largest crash surface in the process and the one a person clicks on; a binding exception
there takes every agent's workspace with it. The inspector was moved out for the same reason (see
[the decision](the-inspector-is-a-client-of-the-broker.md)), and one app for both windows means one
codebase rather than two that drift. The cost is that a window reads everything through
`/operator`, which it needs anyway to be tested and driven by UI automation
([why](a-window-reads-the-broker-over-the-operator-api-and-waits-on-what-a-view-holds.md)).

**Why not keep the window hidden and show it on demand.** A hidden window is still a window in the
broker's process, with the same crash surface, and the start-up window was exactly a hidden-by-
default window that something showed. Removing it is the only way the start-up window cannot come
back.

## What the icon says

**Three states, no animation.** Serving; needs you; not serving. "Working" is not a state: agents are
nearly always working, and an icon that is always moving says nothing. Needs you is a newer release,
a checkout that has moved past the running build, a failure nobody has looked at, or an answer to a
note. Not serving is starting, stopping, or a start that failed -- a port already in use, most
often. The tooltip names the build, then how many agents, workspaces and debugged apps there are, or
why it is not serving, then what is waiting. The menu carries what is waiting as items that deal
with it.

**A start that fails leaves the icon up.** Exiting would leave a person with no word at all about
why RoseMCP is not there after sign-in. The icon stays, not serving, with the reason, one
notification with the log, and Exit.

**Notifications only for what a person can act on.** A new release, once for each version, across
runs; what a restart did to the agents that reconnected across it; Rose failing, with its log, each
failure once and no more than one a minute; and an answer to a note. That list is the whole list.
Everything else waits in the tooltip and the menu. A checkout that has moved on is a menu item and
part of needs you, not a notification: it changes with every commit somebody makes.

**What a restart reports is what this broker can see.** The relays that reconnect introduce
themselves again and say whether their client holds this broker's tools; the tray watches that for
half a minute and then says how many agents came back and whose tools changed. Only relays whose
process outlived the broker before count, so a session started at sign-in is not announced as a
reconnect. What the old broker held -- the debug sessions that ended with it, the tools that changed
by name -- went with it, and each agent hears that on its own first result.

**What the tray remembers is its own file.** The release it announced and the build that ran last
live in `tray.json` beside the settings file, not in it: none of it is a choice somebody made, and
the settings file is read and written whole by every window, so a field the tray rewrote on every
start would be one any window could overwrite with a stale copy.

## What waits for the RoseMCP window

The preferences that had items on the tray's window -- show the inspector on attach, reload a
workspace when its analyzers are rebuilt -- keep working from the settings file; the first has its
toggle in the inspector, the second has none until the RoseMCP window gives it one. So do closing
every workspace and copying the command that starts an inspector by hand. Open RoseMCP and Debug an
app both start the inspector with no session named, which adopts the one debug session there is or
says there are none or several, until the RoseMCP window (#463) and its debug start page (#465)
exist.
