# Only the live-app half is Windows-only

**Decision.** The projects that need Windows target `net10.0-windows`: the live-app host, the tray, the
inspector and `RoseMcp.Ui`. Everything a Roslyn session needs -- `RoseMcp.Contracts`, `.Solutions`,
`.Broker`, `.Worker`, `.Server`, `.XamlDiff` and `.Symbols` -- stays plain `net10.0`. The broker starts
the host as a separate process and never references it, and where the host cannot exist the debug and
XAML tools are not advertised at all.

**Why.** The host is Windows-only by nature: it loads the target's `mscordbi` for ICorDebug and calls
the UWP shell's COM interfaces. Nothing else is. Keeping it behind a process boundary is what lets a
Linux or macOS user run the whole C# surface, and CI publishes and tests that half on Linux so the
claim stays true rather than inferred from the reference lists.

**Why the tools disappear rather than fail.** A declared tool that cannot run is worse than an absent
one: an agent budgets context for it, tries it, and learns nothing useful from the error.
`ToolSurfaceTests` asserts the advertised set on each operating system.

**How it would extend.** A debugger backend for another operating system would be a different host
behind the same broker tools.
