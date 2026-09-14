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

**Why a pipe rather than files in a shared folder.** A reply read from the pipe a request went out on
is that request's answer by construction. Exchanging files means numbering every request and checking
every answer against the number, because "the file exists" cannot tell this answer from the previous
one -- a whole class of bug that a pipe does not have. A read is also a message rather than a file round
trip, which is what an interactive session feels.

**What is still staged on disk.** The provider DLL is copied into a folder per host under
`%TEMP%\RoseMcpXaml`, because injection loads it from a path. That folder gets the AppContainer grant
only when the target runs in an AppContainer; an unpackaged WinUI 3 app needs none. Folders belonging
to hosts that have gone are deleted when the next host starts.

What injecting once per session asks of the provider is in
[xaml-tap-lifecycle](../invariants/xaml-tap-lifecycle.md).
