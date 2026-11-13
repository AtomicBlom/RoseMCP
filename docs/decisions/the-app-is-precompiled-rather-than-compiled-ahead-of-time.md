# The app is precompiled rather than compiled ahead of time

**Decision.** `RoseMcp.App` publishes with ReadyToRun (`PublishReadyToRun`), so its assemblies carry
precompiled code beside their IL, and still runs on the shared .NET runtime. It is not published with
Native AOT, and nothing else in RoseMCP is precompiled.

**Why the app.** It is the one RoseMCP process a person waits on. It ends when its last window closes,
so every click on the tray's icon with no window open, and every agent attaching with the Inspector
wanted, starts it from nothing. Measured from process start on an ARM64 machine, median of warm starts,
to the RoseMCP window showing its first data from the broker:

| | JIT | ReadyToRun |
|---|---|---|
| `Main` | 50 ms | 45 ms |
| `OnLaunched` | 168 ms | 135 ms |
| window activated | 414 ms | 343 ms |
| first frame | 479 ms | 402 ms |
| first data | 521 ms | 442 ms |

Most of the difference is `Microsoft.WinUI.dll`, the WinUI projection, which ships as IL only, so every
XAML type the first window touches is compiled on the way to it. `Microsoft.Windows.SDK.NET.dll`
already ships precompiled. The cost is about 18 MB per architecture, almost all of it that projection,
and the app's assemblies are no longer byte-identical across architectures, so the package carries them
once per architecture rather than once in `shared/`.

**Where the rest goes.** What remains is nearly all WinUI's own: about 50 ms from `Application.Start` to
the `App` constructor, 30 ms creating the window, 75 ms parsing its XAML, 50 ms extending the content
into the title bar, and 90 ms from activating it to the first frame. RoseMCP's own startup -- the
logger, the crash handler, the operator client, the icon, attaching the views -- is about 25 ms of it,
and the first data arrives about 30 ms after the first frame. Loading the three views that start
collapsed only when first shown (`x:Load`) was measured at 11 ms and not taken: it would make every
use of those views conditional for a difference nobody can see.

**Why not Native AOT.** It would not remove the .NET runtime from an install: the worker loads an SDK's
MSBuild, analyzers and generators at run time and cannot be compiled ahead of time, and the server,
the relay and the live-app hosts stay on the runtime with it. The tray hosts the broker in process --
ASP.NET Core, the MCP SDK's attribute-driven tool registration, dependency injection -- so it is the
hardest candidate and the one whose startup matters least, since it starts once at sign-in. For the app
alone it would mean source-generated JSON for everything `OperatorClient` reads, bindings that need no
reflection, and a trim-clean build, on top of a native toolchain for both architectures on every
machine that packages. ReadyToRun takes most of the startup benefit with none of that.

**What would change it.** The remaining cost is starting a process and a XAML framework at all. Keeping
the app running with no window open, so that the next click only shows a window, is the lever left; it
trades that cost for the memory of a resident WinUI process, and is a separate decision.
