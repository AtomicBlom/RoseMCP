# A framework ships its own tap, and hosts the endpoint that loads it

**Decision.** A UI framework that RoseMCP does not ship a tap for ships its own: one managed assembly,
marked with the contract's `RoseMcpTap` attribute and dropped into a tap folder. RoseMCP reads the
attribute from the assembly's metadata and never loads it. The framework hosts a small diagnostics
endpoint inside each app built on it, a pipe named for the app's process id that runs only where the
app opts in. To serve a target no in-box tap serves, the host connects to that pipe and checks that the
target is the process serving it. It then asks the endpoint to load the tap with the same
`workDir|pipe|key` startup data an injected tap receives. From the tap's greeting on, the session is
the one the in-box taps already have: the same pipe, rows and tools.

The protocol, the endpoint and the attribute live in a contract repository of their own,
`external/RoseMcp.Tap`, a git submodule that RoseMCP and every framework compile in as source.

**Why the framework hosts the endpoint.** Attaching to an app that is already running is the case that
matters, and only something already inside the app can be asked to load code into it. XAML's own
`InitializeXamlDiagnosticsEx` is exactly that, hosted by the framework. The endpoint is its managed
counterpart. It loads an assembly into a collectible context and calls a static `Start(string)` found
by name. Compiling it in gives a framework no dependency on RoseMCP.

**Why RoseMCP never loads the tap.** A tap is somebody else's code, and it only means anything inside
the app it serves. Loading it into RoseMCP to ask what it is would run its module initialiser in a
process that reads and rewrites source. Its metadata says everything RoseMCP needs.

**Why the endpoint is checked before it is trusted.** The pipe is named for the target's process id,
and nothing stops another process from creating that name first. Whoever serves it receives the host's
pipe name and key, and could pose as the tap. `GetNamedPipeServerProcessId` has to name the target
before anything is sent.

**Why a dropped-in tap is versioned by a range and declares what it serves.** The in-box taps ship with
the host, so a version mismatch can only be a stale copy, and exact matching refuses it by name. A
dropped-in tap is released apart from RoseMCP, which is the case the
[pipe decision](the-xaml-provider-is-injected-by-the-host-and-answers-on-a-pipe.md) set aside: "if the
provider is ever released on its own, the evolvability argument comes back". It comes back as two
rules rather than a tolerant format:

- The host accepts any version from the first that declared capabilities up to its own, and refuses
  the rest naming which side is behind.
- The version moves only when an existing row changes shape. Anything new is a capability, a name on
  the line after the greeting. A host ignores a name it does not know and treats one that is missing as
  not served.

Rows stay positional and readable in a log, and a tap built against an older contract keeps working
until a row it writes changes.

**Why the host refuses what a tap did not declare, rather than sending it.** A tap answers a command it
does not serve with an empty frame, which reads as a lost message, and a lost edit is one a caller is
tempted to resend. Refusing a command, mode or edit kind by name, before it is sent, tells an agent
what it cannot do on this framework. The tools stay listed, because a tap's capabilities are known only
once it connects.

**Why a contract repository, not RoseMCP's own source.** A framework that compiled RoseMCP's
`XamlWire` in would have to take all of RoseMCP to get one file. It would also carry an Apache-licensed
file into an MIT package. The contract is MIT, holds only what both sides compile, and gets a history
whose tags can name the two versions. The cost is a submodule: every clone, worktree and CI checkout
has to initialise it.

**Alternatives, and why each lost.**

- *A plugin assembly loaded into RoseMCP.* A binary plugin API would either freeze RoseMCP's internal
  types or break every plugin at each release. A crash or hang in the plugin would also be RoseMCP's,
  and a load context does not isolate either.
- *A .NET startup hook set at launch.* It needs no change to the framework, but it cannot reach an app
  that is already running. It remains a later route for a framework with no endpoint, since it is the
  same endpoint code with an entry point.
- *A third native tap.* A managed framework has no COM surface to bind, and native code cannot read
  managed objects without the runtime.
- *The app references the tap and starts it.* It works for attach, but then every app carries the tap
  rather than a tap being dropped in once.
- *Shipping the tap with RoseMCP.* It ties a framework's releases to RoseMCP's, and puts a game
  framework's code in a tool that has nothing to do with it.

**What it does not change.** The in-box taps keep their own connector: injection, sandbox staging,
AppContainer grants and architecture matching all stay as
[hosts-and-deploy](../invariants/hosts-and-deploy.md) describes, applying only to them. They come first
in the choosing, and no dropped-in tap may claim a framework they serve. The live-app host still
requires a debugger, so a framework whose apps cannot be debugged, such as a NativeAOT game, is not
served by this route.
