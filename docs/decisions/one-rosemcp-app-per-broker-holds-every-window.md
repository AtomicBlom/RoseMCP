# One RoseMCP app per broker holds every window

**Decision.** `RoseMcp.App` is the one executable a person's windows live in: the RoseMCP window, organised
by agent, and an Inspector window for each debugged process. It is started by the tray -- Open RoseMCP, a
click on the icon, a notification -- and by the broker when an agent attaches with the Inspector wanted, and
it reads nothing but `/operator`. The command line says which window: nothing, for the RoseMCP window; a
session, a process or `--inspect`, for an Inspector window. It claims a single-instance key per broker --
`RoseMcp.App:<host>:<port>` -- so a second launch against the same broker is handed to the app already
running, which opens or brings forward the window asked for, and the process ends when its last window
closes. Every window reads through one `OperatorClient`.

**Why the Inspector project became the app, rather than a new project beside it.** The design puts both
windows in one app for one reason: one codebase, so the Inspector and the RoseMCP window cannot drift into
two products. A new project beside the Inspector would have been two executables, two single-instance keys
and two places the launch arguments are read -- the drift with a process boundary in it. Keeping the name
`RoseMcp.Inspector` for an app whose first window is not an Inspector would have made every reader of the
layout, the deploy and the logs ask which window is meant. So the project, its install folder (`app`), its
log folder (`App`) and the launcher (`RoseAppLauncher`) are named for what they hold. An install laid down
from an older package still has an `inspector` folder; stopping an install stops both executables, and the
folder is otherwise inert.

**Why one per broker, and not one per machine or one per window.** The Inspector was one window per
debugged process, keyed on the process before it had spoken to the broker. An app that holds several windows
cannot be keyed on one of them. One per machine would hand a launch for a broker on a scratch port to an app
showing the installed broker's agents, which is the confidently wrong answer this repository refuses
everywhere else. The broker's address is known before the app starts, which is what the key must be.

**Why the token is taken from a redirected launch.** A broker mints its token per run (see
[the decision](the-inspector-is-a-client-of-the-broker.md)), so a window left open across a restart is
refused. The tray's next launch is redirected to the running app and carries the new run's token, which
`OperatorClient.Reauthorise` puts on every request from then on. A window that is refused says so and how to
reconnect -- click the icon -- rather than going quiet; one whose broker is not there says Rose is not serving
and reconnects by itself, pausing between tries.

**Why the RoseMCP window reads only while it can be seen.** It holds four long polls -- clients,
workspaces, debug sessions and the newest of the activity record -- because the view list shows a count
from three of them whichever view is open, and two views join the clients to the record. Minimised or
closed, every one is cancelled; a view not on screen does no work on what arrives; and the Activity view's
own poll, on its filter, runs only while it is the view on screen.

**Why a person's actions are operator routes.** Reading a workspace's full status, reloading it and
closing it are `/operator/workspaces/status`, `/reload` and `/close`. The window holds the token and no MCP
session, and closing a workspace every agent shares is the person's call, asked first. Each names a
workspace the broker holds a row for and is refused for any other, so a click never loads a solution
nobody had open; the status read holds the worker without counting as use, and none of it is a tool call,
so the activity record holds none of it.

## What the RoseMCP window leaves to open work

Notes to an agent, and "Ask an agent why" on a workspace's reason, need a route that delivers a note
(#464); the button says it cannot deliver one. "Debug an app" opens an Inspector window that takes the one
session there is until the start page (#465) gives it something to open on.
