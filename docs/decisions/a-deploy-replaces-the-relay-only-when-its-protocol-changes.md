# A deploy replaces the relay only when its protocol changes

**Decision.** The stdio relay a client registers is installed in a folder of its own, `relay/` under
the install root, holding a second publish of `RoseMcp.Server` and a marker naming the relay protocol
it speaks (`RelayProtocol.Version`, mirrored in `tools/published-layout.json`). A promote, the zip's
`install.ps1` and the Inno installer leave that folder, and every relay running from it, alone while
the marker names the protocol they would install; a different protocol is the only thing that stops
those relays and replaces the folder. The broker still restarts on every deploy. The relay notices
the broker going over a request the broker holds open, reconnects to the new one by itself, and
compares a hash of the new broker's tool definitions and server instructions with what its client
last listed: the same means the client hears nothing at the protocol level, different means one
`notifications/tools/list_changed`. Either way the first result after a restart carries one line
saying what happened. The setup command is the relay form.

**The experiment that settled list_changed.** Claude Code's documentation says it re-fetches the tool
list on `list_changed` without a reconnect, and older versions needed one, so this was tried first,
headlessly, against Claude Code 2.1.296. A throwaway stdio server (.NET, ModelContextProtocol 2.2.0)
advertised `tools.listChanged`, offered one tool, and thirty seconds after starting added a second and
announced it. Claude Code was driven with `claude -p --input-format stream-json --output-format
stream-json --mcp-config <file> --strict-mcp-config` across two turns about forty seconds apart. It
called `tools/list` again two milliseconds after the notification, the second turn's tool list
carried the new tool, and the model called it and reported its answer -- no `/mcp`. The server's log
also showed what the relay has to honour: Claude Code speaks the 2026-07-28 revision, opening with
`server/discover` rather than `initialize` and asking for change notifications on a
`subscriptions/listen` stream. A notification sent through the SDK's fan-out for a changed tool
collection arrived tagged with that subscription and worked; so did a bare broadcast, which the
revision forbids for a client that subscribed, so the relay uses the fan-out (`ToolListSignal`).

**Why a fixed folder with a marker.** A folder keyed by the protocol, `relay/v1/`, needs no marker,
but the client registers a path: a protocol change would put the relay somewhere the registration
does not name, and every session would have to be registered again rather than reconnected. A fixed
path with a marker keeps one registration for the life of the install. A relay in the root has to
be stopped by every deploy, since a running executable cannot be overwritten, and a project of its own (`RoseMcp.Relay`) would have duplicated what the relay already
takes from the broker library for the cost of one more thing in the layout. The marker is written
when the relay is published, so the zip, the installer and a promote carry the same one and an install
compares a file rather than loading an assembly Windows PowerShell 5.1 cannot. The relay is Windows
only: elsewhere a running file can be replaced, and a Linux client registers the server in the root.

**What the relay protocol is.** Everything a relay and a broker agree on beyond MCP: the headers a
relay names itself with, `/admin/relays` and `/admin/relays/watch` and what they answer, the
`_meta` keys a relay sends, and what an instance id means. MCP between two builds of Rose is the same
protocol however much the tools change, so a relay from an older deploy forwards for a newer broker
perfectly well until this changes. Raise `RelayProtocol.Version` and the layout's `protocol` together
-- a test holds them equal -- with any change an older relay would misread. A relay that meets a broker
speaking another protocol says so in the line and in its log, since no reconnect can mend it.

**Noticing the broker go.** The relay holds `GET /admin/relays/watch` open. The broker writes one
line, which instance it is, and nothing more until it stops; a killed tray -- which is what a deploy
does -- closes the socket, so the relay knows at once, with nothing on a timer. Reconnecting only on
the next call would leave an idle session not hearing that its tools changed until it next called,
with the wrong tools in hand; polling would add a request and its log lines every few seconds per
session to a tray log that is already large. A watch that ends is a reason to look rather than proof:
the relay asks the broker first, because reconnecting to one that is still there opens a new MCP
session and the debug sessions belong to the old one. The 2026-07-28 revision has no `ping`, so any
answer counts, a refusal included. That is why a watch whose first line names another instance is
not asked about: the broker restarted between the relay connecting and the watch arriving, the old
connection reaches the new broker on the same endpoint, which refuses it -- an answer -- and asking
would keep the relay on that connection for good. The watch says why it ended, and that one
reconnects at once. Reconnecting loops, two seconds apart, for as long as the relay
lives, since a tray mid-deploy is down for as long as the copy takes. Calls and the watch share one
reconnect, and the comparison happens inside it, so one restart is one comparison and at most one
notification.

**Restart or reconnect.** The broker mints an instance id when it starts and returns it from
`/admin/relays`. A build cannot tell the two apart -- a tray restarted without a deploy is the same
build, and its solutions and debug sessions are gone all the same -- so the line says "restarted" only
for a new instance, and otherwise that the connection was opened again.

**What is compared.** Each tool's canonical JSON -- the SDK's serialisation, keys sorted, `_meta` left
out -- hashed, plus the instructions with their line endings normalised. Two builds that emit the same
schema in another order compare equal, and a changed description, schema or annotation does not.
The baseline is what the client holds: the listing it last made through the relay, and the
instructions it was given when it connected.

**Instructions arrive only once.** A client reads server instructions when it connects -- the
`initialize` or `server/discover` answer -- and Claude Code did not discover again after
`list_changed`. A change to the instructions alone still sends the notification, the one signal
there is, and the line says that reconnecting picks them up; it is then counted as told, or every
later reconnect would say it again. The relay gives the broker's instructions as its own when it
starts; without that a relayed session would get none, since its client never sees the broker's
handshake.

**The line, and what it can honestly say.** One line on the first result after a reconnect -- claimed
once a result is in hand, by exactly one of the calls in flight -- naming the new build, that
solutions load again on the first call that needs them, which debug sessions ended, and which tools
were added, removed or changed. Debug sessions die with the broker, so they are read while it lives:
after every call that starts or ends one, the relay asks `rose_debug_list`, which answers with the
sessions the asking connection started, and every call a session makes goes through the relay's one
connection. A session whose host died between those readings is still named, as one this session
had open, which it was. Two restarts before any result fold into one line.

**With no tray.** An installed relay that finds no tray does not host the broker from its own
assemblies, which are as old as the last relay protocol change: every tool would answer from that
old build, and its workers would hold the install's files through the next deploy. It starts the
install root's server as a child with `--no-relay` and relays to it, so the code answering is what
the last deploy put down. The child runs out of the files a deploy replaces, so the deploy stops it,
and the relay ends with it rather than starting another, which would hold those files again while
the copy was still under way. That session needs `/mcp`, and the deploy says so: it is the one case
that still costs a connection, and the reason the tray is the way to run RoseMCP. A relay run from
anywhere but an install's relay folder -- a build output, a Linux install -- hosts the broker itself
as it always has.

**Counting the relays back.** A relay is counted by the broker while its watch is open, by its own id,
so the count goes down when a relay goes. After restarting the tray, a deploy waits up to twenty
seconds for that count to reach the relays still running from the install and says how many
reconnected, instead of telling the user to `/mcp`. Sessions registered with the root's server, or
relaying to a child broker, are stopped and named as needing `/mcp`.

**Who registers it.** The tray shows the relay form, as do `install.ps1 -Register`, the Inno finish
page and the README: `claude mcp add rose -s user -- "<install>\relay\RoseMcp.Server.exe"`, user
scope because the relay finds each session's solution from the directory it starts in. A client
registered with the http endpoint keeps working and gets none of this.
