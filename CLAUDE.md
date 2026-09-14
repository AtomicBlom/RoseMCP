# RoseMCP

An MCP server that gives coding agents real Roslyn semantics over a loaded C# solution:
diagnostics, navigation, source-generated code, and refactorings. It is built using itself --
see **Dogfooding is the point** below, which is the development method and not a slogan.

What it exists to fix, how it got here, and the case for it are on the
[wiki](https://github.com/AtomicBlom/RoseMCP/wiki).

## Architecture

```
client --stdio or http--> RoseMcp.Server (broker, no Roslyn refs)
   or RoseMcp.Tray    -->    |  one child per solution, MCP over stdio
   (hosts it in-process)     +--> RoseMcp.Worker --solution D:\a\A.sln
                             +--> RoseMcp.Worker --solution D:\b\B.slnx

client --stdio--> RoseMcp.Server --http--> RoseMcp.Tray --> the tray's workers
                  (TrayRelay, no workers of its own)
```

| Project | What it is |
|---|---|
| `RoseMcp.Contracts` | DTOs and tool-name constants shared by broker and worker. No package references at all, which is what lets every host reference it. |
| `RoseMcp.Solutions` | Reads solution files and `rosemcp.json` without MSBuild or Roslyn, so the broker can decide *which* solution a call means without depending on the thing that loads one. Derives the short workspace key. |
| `RoseMcp.Settings` | What a person has chosen about how RoseMCP behaves, per machine rather than per session or per solution. |
| `RoseMcp.Logging` | The file sink, referenced only by the launchable hosts so Serilog stays off the DTO assembly and the tests. |
| `RoseMcp.Symbols` | Reads a module's metadata and its portable PDB: method tokens, local names at an instruction, the line an IL offset came from, and which compiled methods make up a body somebody is reading. Knows nothing about a debugger; it reads files. |
| `RoseMcp.Broker` | `WorkspaceManager`, worker supervision, the tool layer, the activity log, and `AddRoseMcpBroker()`. One registration path, used by both hosts. |
| `RoseMcp.Server` | Console host. `--transport stdio` (default) or `--transport http`. |
| `RoseMcp.Worker` | Owns exactly one `MSBuildWorkspace`. All Roslyn work happens here. |
| `RoseMcp.XamlStubs` | The XAML stub generator, loaded by the worker as an analyzer assembly rather than referenced as a library. |
| `RoseMcp.XamlDiff` | Takes markup apart for the live-edit path. Plain `net10.0`, so a test can see inside it. |
| `RoseMcp.LiveApp` | The live-app host: one ICorDebug session and one XAML diagnostics session, for one debugged process. |
| `RoseMcp.Xaml.Tap` | The native in-app provider, shared between frameworks: the tap, the overlay, the pipe. Headers only. |
| `RoseMcp.Xaml.Uwp.Tap`, `RoseMcp.Xaml.WinUi.Tap` | The two bindings of that provider, one per XAML framework. Which one serves a target is decided by the framework the target runs. |
| `RoseMcp.Ui.Core` | The half of both windows that is not WinUI: rows, formatting, the poll loop, the in-place merge, and `OperatorClient`. Plain `net10.0`, so it runs in the fast suite. |
| `RoseMcp.Ui` | WinUI class library. Themes, window chrome, the crash handler and the icon assets, shared so a second window is the same product rather than a lookalike. |
| `RoseMcp.Tray` | WinUI 3 tray app for http mode. Hosts the broker in-process, so its window reads the live `WorkspaceManager` rather than a pushed copy. |
| `RoseMcp.Inspector` | WinUI 3 window for one debugged process. A **client** of the broker over the http operator API, owning no session of its own, because a process has one debugger. See [the decision](docs/decisions/the-inspector-is-a-client-of-the-broker.md). |

Logic goes in `Contracts` only when a test needs it and the host that owns it cannot be referenced.
`XamlStackModules`, `ToolArgumentShape`, `XamlProviderPath`, `ValuePath`, `SymbolLocation` and
`HostVersion` are the whole list, each a pure function over strings or JSON with the host's own facts
passed in. Three of the launchable hosts are `net10.0-windows` or reachable only as a child process,
so a rule living beside its host is a rule no test can see. It is not a licence for behaviour:
anything holding state, touching Roslyn, or knowing what a tool does belongs in the host.

The worker is a separate process because analyzer and generator assemblies cannot be unloaded
once loaded, MSBuild resolution is per-process, and killing a worker is the only reliable way to
reclaim memory or pick up a rebuilt generator.

## Rules that bind everywhere

These are the ones you can break without going anywhere near the subsystem that owns them, which is
why they are here and the rest are behind a trigger.

- **Nothing writes to stdout in stdio mode** except protocol frames. All logging goes to stderr and
  to a file. A stray `Console.WriteLine` corrupts the stream, and the failure looks like a protocol
  bug rather than a print statement.
- **Reads never observe a snapshot older than disk.** If you add a read path, it goes through the
  `WorkspaceSession` barrier. No exceptions.
- **Every result carries a `revision` and names the workspace that answered.** Attribution is added
  once, in `WorkspaceManager`, so a tool added later cannot forget it.
- **An error says what went wrong, not that something did.** Convert at the MCP boundary, never at
  the throw site: the exception type carries meaning further in, and retry decisions turn on it.
- **Whatever writes C# has to end formatted**, both passes, or IDE0055 fails the build over line
  endings the formatter did not have a reason to touch.

## Invariants

Each rule below is a way of getting a confidently wrong answer rather than a failure, and each is
written out with its reasoning in `docs/invariants/`. Find the row covering what you are about to
touch and read that file first.

| Read this | Before touching |
|---|---|
| [transport-and-lifetime.md](docs/invariants/transport-and-lifetime.md) | stdio or http transport, `TrayRelay`, progress, cancellation, anything that ends a process |
| [workspace-freshness.md](docs/invariants/workspace-freshness.md) | a read path, a reload trigger, the file watcher |
| [solution-routing.md](docs/invariants/solution-routing.md) | `SolutionResolver`, `WorkspaceManager.WorkspaceFor`, `BuildProperties`, `rosemcp.json` |
| [result-shapes.md](docs/invariants/result-shapes.md) | a new tool, a new field on a result, an error path |
| [writing-csharp.md](docs/invariants/writing-csharp.md) | anything under `src/RoseMcp.Worker/` that emits or rewrites source |
| [analyzers-and-generators.md](docs/invariants/analyzers-and-generators.md) | analyzer loading, `RoseMcp.XamlStubs`, anything handing Roslyn an `AnalyzerReference` |
| [xaml-live-edit.md](docs/invariants/xaml-live-edit.md) | `rose_xaml_*`, `src/RoseMcp.XamlDiff/`, the apply path in `src/RoseMcp.LiveApp/Xaml/` |
| [xaml-tap-lifecycle.md](docs/invariants/xaml-tap-lifecycle.md) | `tap_object.h`, injection, anything that advises the visual tree |
| [overlay.md](docs/invariants/overlay.md) | `tap_overlay.h`, `tap_measure.h` |
| [hosts-and-deploy.md](docs/invariants/hosts-and-deploy.md) | `XamlStackModules`, architecture detection, `tools/deploy.ps1`, what an install carries |
| [live-app-tests.md](docs/invariants/live-app-tests.md) | any live-app test or fixture |

A new invariant goes in the file whose trigger already covers it, or in a new file with a trigger of
its own. This file grows only when a rule binds everywhere.

## Where things are written down

- **Decisions** go in `docs/decisions/`, one file per decision, named for the decision rather than
  numbered: what was chosen, and why the alternatives lost. An invariant that follows from a decision
  links to its record rather than arguing it again.
- **Invariants** go in `docs/invariants/`, as above: a rule a change could break, and the failure it
  prevents.
- **Explanations for people using RoseMCP** go on the [wiki](https://github.com/AtomicBlom/RoseMCP/wiki).
  Decisions and invariants never do, because they constrain the code and have to change in the same
  commit as it.

## Commands

```
dotnet build RoseMcp.slnx
dotnet test
dotnet format                      # run before every commit
dotnet format --verify-no-changes  # what CI checks
```

Run it:

```
dotnet run --project src/RoseMcp.Server                                  # stdio (default)
dotnet run --project src/RoseMcp.Server -- --transport http --port 5077  # http + /admin/workspaces
dotnet run --project src/RoseMcp.Tray                                    # tray UI, hosts the broker
```

The broker finds the worker binary next to itself, then via `ROSEMCP_WORKER`, then in the sibling
project output, so running from source works without publishing first. Non-loopback http binds are
refused unless `ROSEMCP_TOKEN` is set.

Deploy over the running instance, or build release zips:

```
./tools/deploy.ps1                          # stop tray, publish, restart
./tools/deploy.ps1 -Mode package            # artifacts/rosemcp-win-{x64,arm64}.zip
```

`promote` installs to `-Destination`, else `ROSEMCP_DEPLOY_ROOT`, else
`%LOCALAPPDATA%/BinaryVibrance/RoseMCP` -- the same vendor/product folder the logs live under.
Where a machine keeps its install is that machine's business, so no path is committed here.

Tests are split by what they cost. `RoseMcp.UnitTests` runs no MSBuild and starts no child process,
and finishes in a couple of seconds, so it is worth running on every change. It does touch disk, in
the handful of tests that write a temp file or stage a directory layout to prove a path is read the
way the code says. `RoseMcp.IntegrationTests` loads real solutions from `tests/fixtures`, runs real
design-time builds and starts real workers, and takes minutes rather than seconds -- most of it the
live-app suite in `LiveAppSessionTests`. `RoseMcp.TestSupport` holds the doubles both need. Put a
test where its cost puts it: a test that needs a `FixtureSolution` or a `TestSession` is an
integration test however small it looks.

`dotnet test` needs the `global.json` opt-in already in the repo: TUnit runs on
Microsoft.Testing.Platform, and the .NET 10 SDK no longer bridges that through VSTest -- without the
opt-in it refuses outright, naming the VSTest target.

**Never pass `--nologo` to `dotnet test` here.** It is a VSTest option, Microsoft.Testing.Platform
does not recognise it, and an unrecognised option is reported as `Zero tests ran` with exit code 5 --
which reads exactly like a discovery failure and sends you looking at the runner, the source
generator and the project file in turn. The banner-suppressing equivalent is `--no-banner`
(dotnet/sdk#55309). Individual test projects are also executables, so running one directly works too
-- and that is how you run just the fast half:

```
./tests/RoseMcp.UnitTests/bin/Debug/net10.0/RoseMcp.UnitTests.exe
./tests/RoseMcp.IntegrationTests/bin/Debug/net10.0/RoseMcp.IntegrationTests.exe --treenode-filter '/*/*/RenameTests/*'
```

Run a worker standalone against a fixture -- the fastest way to debug Roslyn behaviour without
the broker in the way:

```
dotnet run --project src/RoseMcp.Worker -- --solution tests/fixtures/WithGenerator/WithGenerator.slnx
```

A solution whose configurations are not `Debug`/`Release` needs to be told which one, and anything
whose target framework is chosen by some other property needs that property:

```
dotnet run --project src/RoseMcp.Worker -- --solution D:/repo/A.slnx --configuration Debug-2027
dotnet run --project src/RoseMcp.Worker -- --solution D:/repo/A.slnx -c Release -p RevitVersion=2027
```

## Dogfooding is the point, not a nicety

This repository is the first consumer of its own tools, and that is the whole development method.
**If Rose does not make creating, editing and refactoring C# better than grep and find-and-replace,
it has little reason to exist** -- the navigation and diagnostics are worth something on their own,
but not enough to justify a warm Roslyn process per solution. The only way to know whether it clears
that bar is to build Rose using Rose.

So, working in this repository:

- **If Rose provides an action, use it.** `rose_find_references` rather than grep for usages,
  `rose_rename_symbol` rather than find-and-replace, `rose_diagnostics` rather than a build to see
  whether something compiles, `rose_symbol_info` rather than reading a file to learn a type.
- **If it fails, or is worse than the thing it replaces, that is a defect in Rose.** Not an
  inconvenience to route around quietly. File it, with what you were trying to do and what the tool
  did instead.
- **The workaround is allowed; the silence is not.** Mid-task, reach for `sed` and get unblocked --
  but the finding is the valuable part of having hit it, and it is worthless unfiled.
- **A tool nobody reaches for is a bug of the same severity as one that returns wrong answers.** If
  the tool exists, works, and still lost to grep, the reason it lost is the finding. Usually the
  description, the argument shape, or a setup step nobody wants to pay.

## Conventions

Enforced by `.editorconfig` where the analyzer can express them, by review where it cannot.

- **Tabs**, not spaces.
- **File-scoped namespaces**, matching the folder they live in (IDE0130). A directory rename is
  otherwise invisible to the compiler.
- **Braces on their own line** -- Allman, everywhere.
- **A body on its own line gets braces.** A single simple statement kept on the same line as the
  condition may go without them, whatever that statement is; anything that wraps is braced.

  ```csharp
  if (document is null) return null;             // fine
  if (File.Exists(candidate)) yield return candidate;   // also fine -- not only control flow
  if (!TryResolve(path, out var project))
  {
      return WorkspaceResult.NotFound(path);     // a body on the next line is always braced
  }
  ```

  `.editorconfig` can only express `csharp_prefer_braces = when_multiline`, which allows the
  unbraced next-line body this forbids. The rule above is the intent, and review is what enforces
  the difference.
- **Readable `if` statements.** Prefer an early-return guard over nesting; hoist a compound
  condition into a named local `bool` rather than packing three clauses into the `if`.

  ```csharp
  var isStructuralChange = projectFileChanged || solutionFileChanged;
  if (isStructuralChange)
  ```
- `nullable enable`, warnings as errors, latest language version.
- **Comments are self-contained and present tense.** A comment says what the code does, the
  invariant a caller relies on, or *why this and not that*. It never says when it was written, what
  the code was before, or which planning document discussed it.
  - **No history or schedule.** Not `used to`, `previously`, `no longer`, `for now`, `until now`,
    `today`, `a later slice`, `lands in`, `comes in a later issue`. Describe the failure the code
    prevents as a consequence of not having the code, which is timeless, rather than as a past
    event, which is not. `Nothing used to remove it and the folders accumulated` becomes `the
    sandbox folder goes when the host does; one that outlives its host accumulates a copy of the
    provider and a grant to ALL APPLICATION PACKAGES`.
  - **No decision or milestone numbers** (`D14`, `D36`, `M13`, `§8`). Restate the reason in a
    sentence, or link the decision page.
  - **No issue or pull-request numbers unless they name open work the reader has to tolerate** --
    a transitional state, an accumulation, a pending fix. Then write the number and the fact
    together: `taps are never unadvised (#68), so this must be idempotent`. A closed issue's number
    is a tag: drop it and keep the explanation. If the explanation cannot stand without the number,
    rewrite it until it can.
  - **Measurements stay only when the code depends on the number** -- a timing behind a constant, a
    count that made something a lock rather than a documented limitation. "It was measured" with no
    number is a claim, and a number with no decision hanging on it is a story. Customer paths and
    repository names are evidence, not reasons.
  - **Long "why" is welcome**, in the shape "X, because Y", and a non-obvious algorithm or gotcha
    earns as many lines as it needs. If a paragraph only makes sense against what the code used to
    do, it is a commit message.
  - **Public types and members keep an XML summary.** A private member gets one when the reason it
    exists is not visible from its code. A class summary describes the class as it is, not the slice
    it began as.

Commit at every milestone boundary and whenever a self-contained piece works. Run `dotnet format`
first so formatting never shows up as diff noise.
