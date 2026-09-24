# The XAML provider is injected by the host and answers on a named pipe

**Decision.** The live-app host injects the native XAML provider into its target with
`InitializeXamlDiagnosticsEx`; there is no separate injector process. The host creates a named pipe,
`rosemcp-xaml-{host pid}-{guid}`, passes its name in the injection's initialisation data, and the
provider connects back to it. After that, every request is a length-prefixed frame on the pipe.

**Why the host injects.** It already runs in the target's architecture and already holds its process
id, which are the two things injecting needs.

**Why a pipe works from inside an AppContainer.** Creating a pipe from inside the sandbox is the hard
direction; connecting to one that already grants your identity is not. The host's pipe gives the
current user full control and grants read and write to ALL APPLICATION PACKAGES (`S-1-15-2-1`) and ALL
RESTRICTED APPLICATION PACKAGES (`S-1-15-2-2`), which is the only identity a provider inside a packaged
app has. The loopback restriction that makes the tray's http port awkward to reach from an app applies
to sockets, not pipes. "UWP cannot use named pipes" is a Store certification rule about submitted
packages, and an injected diagnostics DLL is in nobody's package.

**Why the provider presents a key.** That grant is to every packaged app on the machine, not to the
target, and a pipe with one server instance belongs to whatever connects first. So reaching the pipe
proves nothing, and the host mints a key per session, hands it over in the initialisation data, and
refuses a provider that does not greet with it. What connects first can still hold the pipe; it can no
longer answer a tree read with rows of its own making.

**Why a pipe rather than files in a shared folder.** Exchanging files means every handshake is "does
the file exist", which cannot tell this answer from the previous one, so every exchange has to be
numbered and checked -- a whole class of bug that a pipe does not have. A read is also a message rather
than a file round trip, which is what an interactive session feels. The pipe does not remove numbering
altogether: a reply is its request's answer only while nothing times out, and a request the host gave
up on is still served, so each request carries an id its reply echoes. That is one number checked in
one place, where files needed it for every exchange.

**Why the payload is tab-separated text, rather than JSON or a binary format.** Rows of escaped,
tab-separated fields, one contract in both directions (`XamlWire` on the host, `tap_channel.h` in the
provider). The alternatives were weighed on the only grounds that could decide them, and neither is
speed. A production UWP app's whole tree -- 5,971 elements, roughly 700 KB in this format -- crosses
the pipe and is parsed into records in 76 ms end to end, and at a named pipe's throughput the transfer
is about a millisecond of that; the rest is getting onto the app's UI thread and walking. Binary would
save part of a millisecond, and JSON would add roughly as much again by repeating every field name on
every row. Both are unmeasurable against the total.

What JSON would buy is evolvability: an unknown field is ignored rather than shifting every column
after it. That matters when the two ends are released apart, and these are not -- the provider ships
beside the host that stages it, so two formats meet only through a stale copy, and there a tolerant
format is the wrong answer: it reads the stale copy's rows as data. A protocol version in the greeting,
refused on mismatch, is what a lock-step pair needs. Text also stays readable in a log, which is how
this channel's failures have been diagnosed. If the provider is ever released on its own, the
evolvability argument comes back, and `XamlWire` is the one place the format would change.

What does cost time at scale is the tree read itself: it serialises every element whatever was asked
for, and the host pages and filters afterwards (#322).

**What is still staged on disk.** The provider DLL is copied into a folder per host under
`%TEMP%\RoseMcpXaml`, because injection loads it from a path. That folder gets the AppContainer grant
only when the target runs in an AppContainer; an unpackaged WinUI 3 app needs none. Folders belonging
to hosts that have gone are deleted when the next host starts.

What injecting once per session asks of the provider is in
[xaml-tap-lifecycle](../invariants/xaml-tap-lifecycle.md).
