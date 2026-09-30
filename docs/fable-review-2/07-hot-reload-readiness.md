# Hot-reload readiness: distance, and the path

**Scope.** `src/RoseMcp.Worker/BuildFreshness.cs`, `src/RoseMcp.Worker/WorkspaceSession.cs` (via
02's facts), `src/RoseMcp.LiveApp/Debugging/CorDebugSession.cs`, `src/RoseMcp.LiveApp/Debugging/Uwp.cs`,
`src/RoseMcp.LiveApp/Xaml/XamlDiagnosticsSession.cs`, `src/RoseMcp.XamlDiff/XamlApplyBaseline.cs`,
`src/RoseMcp.Contracts/LiveXamlApplyResult.cs`, `src/RoseMcp.Contracts/LiveXamlEditResult.cs`,
`src/RoseMcp.Broker/LiveAppSessionManager.cs`, `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs`,
`Directory.Packages.props`, the four decision/invariant pages named in the brief, and the
"Hot-reload relevant facts" sections of `02-worker-roslyn.md` (594-653) and
`03-liveapp-debugger-and-tap.md` (603-702). Externally: the .NET 10.0.302 SDK's `dotnet-watch`
folder, `ClrDebug 0.4.2`'s XML docs, the `Microsoft.NETCore.App.Ref` 10.0.10 ref pack, nuget.org's
flat index, and learn.microsoft.com.

**Verdict.** **Further than the codebase's shape suggests, and closer than the amount of new code
suggests -- call it fragile-but-well-aimed.** Every hard part RoseMCP has already solved is on the
transport and lifecycle side: a warm `Solution` per solution, a barrier that makes reads never older
than disk, a freshness check that is exactly the initial-baseline precondition, a debugger attached
from birth that sees every module load, a stop/resume machine, a portable-PDB reader, and -- decisive
-- a *working precedent for the whole apply shape* in the XAML live edit: baseline-of-what-was-sent,
compute outside the target, send a batch, per-edit outcomes, never retry. What is missing is not
plumbing but the two halves nobody has written: the **delta computation** (Roslyn's EnC analyzer,
which is internal and, as of Roslyn 5.6, behind a strong-named `InternalsVisibleTo` that names
Microsoft assemblies only) and the **apply channel into the target** (either ICorDebug `ApplyChanges`
with the JIT flag set at `LoadModule` -- a callback RoseMCP already handles and already throws away
the module handle from -- or a managed startup-hook agent, which RoseMCP has no precedent for at
all). Neither is research; both are weeks. The honest number is **5-8 weeks of focused work for a
credible v1 on plain .NET + WinUI 3 unpackaged**, against roughly one week for the XAML live-edit
epic (#11/#12, 2026-09-02 to 2026-09-05) -- because the XAML epic reused a framework-provided apply
API and this one has to build its own.

## What exists today

Read "present" as: the code does this now, for its own reasons, and hot reload can use it unchanged.
"Partial" as: the mechanism exists but is shaped for something else.

| Capability hot reload needs | State | Where |
|---|---|---|
| Launch an exe under the debugger from birth | **present** | `RuntimeAttachment.cs:76-96` (`CreateProcessForLaunch(..., bSuspendProcess: true, ...)`), `RuntimeAttachment.cs:131-175` (`AttachAtSuspendedStartup`) |
| Launch with a *controlled environment* (exe) | **absent** | `RuntimeAttachment.cs:81` passes `lpEnvironment = IntPtr.Zero`; the target inherits the broker's environment |
| Launch with a controlled environment (UWP) | **present** | `Uwp.cs:106` `ActivationEnvironment = "ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1\0\0"`, passed at `Uwp.cs:118-133` via `IPackageDebugSettings::EnableDebugging` |
| Attach to an already-running process | **present**, and **fatal to architecture A** | `RuntimeAttachment.cs:52-70`; JIT flags can only be set inside `LoadModule`, which has already gone by |
| Module-load hook to hang baseline capture off | **present** | `Record` -> `LoadModuleCorDebugManagedCallbackEventArgs` at `CorDebugSession.cs:750` -> `BindModule` `:1345` -> `BreakpointTable.BindNewModule` |
| Module **handle** registry (`ICorDebugModule` per path) | **absent** | `TargetSymbols.Remember` (`TargetSymbols.cs:44-54`) keeps paths (strings) and drops the `CorDebugModule`; LIV-05 wants the same map for binding |
| JIT flags for EnC (`CORDEBUG_JIT_ENABLE_ENC`, `CORDEBUG_JIT_DISABLE_OPTIMIZATION`) | **absent** | nothing calls `SetJITCompilerFlags` or `SetDesiredNGENCompilerFlags` anywhere in `src` |
| Baseline capture: module metadata | **partial** | `RoseMcp.Symbols` reads module metadata off disk (`SymbolCache.cs:33-56`); Roslyn's `ModuleMetadata.CreateFromFile` would read `Project.OutputFilePath` (`BuildFreshness.cs:40`) |
| Baseline capture: portable PDB / `EditAndContinueMethodDebugInformation` | **partial** | `PortablePdb.Extents`/`SequencePointsOf` (`PortablePdb.cs:121-186`) read the same file, for different tables |
| Freshness precondition ("is bin this code") | **present** | `BuildFreshness.Of` (`BuildFreshness.cs:24-36`), `Stale == false` per project; exposed as `rose_build_freshness` |
| Solution snapshot identity (baseline versus now) | **partial** | one `_revision` per *solution*, advanced by both writes and disk reconciliation (`WorkspaceSession.cs:146`, `:284`, `:389`); per-project change key exists as `Project.GetDependentSemanticVersionAsync` (`DiagnosticsService.cs:104`) |
| Pinning the baseline `Solution` | **absent, and cheap** | `WorkspaceSession._current` (`:53`) is dropped on every adoption; a `Solution` is immutable and shares trees, so retaining one costs little |
| Delta computation -- emit half | **available, unused** | `Compilation.EmitDifference`, `EmitBaseline.CreateInitialBaseline`, `SemanticEdit`: public in the pinned `Microsoft.CodeAnalysis` 5.9.0; zero uses in `src` |
| Delta computation -- analysis half (syntax diff -> `SemanticEdit`) | **absent, and not ours** | `internal` to `Microsoft.CodeAnalysis.Features`; see §3 |
| Rude-edit classification | **absent** | same place |
| Delta transport worker -> broker -> host | **partial** | the pipes exist (`WorkspaceWorker`, `LiveAppSession`, both MCP over stdio); nothing carries bytes today -- XAML crosses as text |
| Apply -- debugger path | **absent** (API available) | `ClrDebug 0.4.2` exposes `CorDebugModule.ApplyChanges(Int32, IntPtr, Int32, IntPtr)` and `TryApplyChanges` |
| Apply -- agent path | **absent, no precedent** | no startup hook, no `CreateRemoteThread`, no managed injection anywhere in `src` |
| Process synchronised for an apply | **present** | `Break(int?)` `:868-922`, `ContinueInternal` `:1327-1380`, the `_gate`/callback discipline `:1497-1562` |
| PDB delta into `RoseMcp.Symbols` | **absent** | `SymbolCache` models *disk*, invalidating on file stamp (`SymbolCache.cs:33-56`); a process baseline plus accumulated deltas is not representable |
| Active-frame remap | **absent** (API available) | `ClrDebug` exposes `CorDebugManagedCallback.OnFunctionRemapOpportunity`/`OnFunctionRemapComplete` and `CorDebugILFrame.RemapFunction(Int32)`; `OnEvent` (`:1497`) subscribes `OnAnyEvent` and `Record` (`:1565-1606`) has no arm for either |
| Framework refresh (`MetadataUpdateHandler`) | **n/a to the debugger path; absent on the agent path** | runtime invokes handlers only from `MetadataUpdater.ApplyUpdate`, not from `ICorDebugModule2::ApplyChanges` |
| Result shape to the agent | **modelled, not built** | `LiveXamlApplyResult` + `LiveXamlEditResult` (per-edit `Kind`/`Target`/`Status`, `Notes`, `Detail`) are exactly the shape a `LiveCodeApplyResult` wants |
| Tests / probe | **partial** | `tests/DebugProbeTarget/Program.cs` is a live, debuggable target with `NoInlining`/`NoOptimization` methods (`:62-64`); no EnC fixture, no rude-edit corpus |
| Pairing a workspace with a live-app session | **absent by design** | `LiveAppSessionManager` is "the debugging counterpart to `WorkspaceManager`... tracked separately" (`LiveAppSessionManager.cs:10-18`); nothing joins a solution to a pid |

Nine present, eight partial, eleven absent. The absences cluster in exactly two places: **delta
computation** and **the apply channel**.

## The two architectures

### A. Debugger-driven Edit and Continue (ICorDebug `ApplyChanges`)

The host already owns an `ICorDebug` session, so the apply is one call on a module handle it already
sees and then discards. **Verified locally:** `ClrDebug 0.4.2` (the pinned version,
`Directory.Packages.props`, "Debugging (live-app host)") exposes, in
`D:\NuGet\clrdebug\0.4.2\lib\net8.0\ClrDebug.xml`:

- `CorDebugModule.ApplyChanges(Int32, IntPtr, Int32, IntPtr)` and `TryApplyChanges(...)`
- `CorDebugModule.TrySetJITCompilerFlags(CorDebugJITCompilerFlags)`; the flags
  `CORDEBUG_JIT_ENABLE_ENC` and `CORDEBUG_JIT_DISABLE_OPTIMIZATION` both exist on that enum
- `CorDebugProcess.TrySetDesiredNGENCompilerFlags(CorDebugJITCompilerFlags)`
- `CorDebugILFrame.RemapFunction(Int32)` / `TryRemapFunction(Int32)`
- `CorDebugManagedCallback.OnFunctionRemapOpportunity` and `.OnFunctionRemapComplete`, with
  `FunctionRemapOpportunityCorDebugManagedCallbackEventArgs` carrying `OldFunction`, `NewFunction`
  and `OldILOffset`

So the whole EnC surface is already in the package the host references. Nothing has to be P/Invoked
by hand.

**What it costs.** Three things, in descending order of awkwardness.

1. **`SetJITCompilerFlags` is callable only inside the `LoadModule` callback for that module.**
   *Verified on the web* (learn.microsoft.com, `ICorDebugModule2::SetJITCompilerFlags`): it "can be
   called only from within the `ICorDebugManagedCallback::LoadModule` callback for the module", and
   calls after that callback has been delivered fail. RoseMCP handles exactly that callback
   (`CorDebugSession.cs:750`), so the hook is in the right place -- but it means
   **`rose_debug_attach` to a running process can never hot-reload the modules already loaded**, and
   launch or startup-attach is the only route. That is a product statement, not an implementation
   detail, and it needs saying in the tool description rather than discovered.
2. **The target must be synchronised for the apply.** `Break` / `ContinueInternal` already give that,
   and `TargetSymbols.Walk` (`TargetSymbols.cs:65`) is the worked example of that stop/continue pair.
   But "the app freezes for a moment on every edit" is a visible difference from what people mean by
   hot reload.
3. **Framework `MetadataUpdateHandler`s do not fire.** The runtime invokes them from
   `MetadataUpdater.ApplyUpdate`, not from the debugger path. So a WinUI page whose generated code
   changed would have new IL and no re-render. For a method-body edit that is fine; for anything a
   framework caches it is not.

Set against that: **no new process-side machinery at all**, and the debugger is already how RoseMCP
gets into a UWP app at birth.

### B. The dotnet-watch model (managed agent + `MetadataUpdater.ApplyUpdate`)

A startup hook loads a small managed assembly into the target, which listens on a pipe and calls
`MetadataUpdater.ApplyUpdate`. **Verified locally**, by reading the metadata of
`...\sdk\10.0.302\DotnetTools\dotnet-watch\10.0.302\tools\net10.0\any\hotreload\net10.0\Microsoft.Extensions.DotNetDeltaApplier.dll`:
a type literally named `StartupHook`, plus `Microsoft.DotNet.HotReload.HotReloadAgent`,
`MetadataUpdateHandlerInvoker` (with nested `ClearCache`/`UpdateApplication` action types),
`NamedPipeTransport`, `WebSocketTransport`, `ManagedCodeUpdateRequest`, `UpdateResponse`. Every one
of those types is `internal`. Its UTF-16 string literals include `DOTNET_STARTUP_HOOKS` and
`DOTNET_WATCH_HOTRELOAD_NAMEDPIPE_NAME`; `DOTNET_MODIFIABLE_ASSEMBLIES` appears in
`Microsoft.DotNet.HotReload.Watch.dll` (the watcher side, which sets it on the target it launches).

**Verified on the web** (learn.microsoft.com, net-10.0):
`System.Reflection.Metadata.MetadataUpdater` lives in `System.Runtime.Loader.dll` with
`ApplyUpdate(Assembly, ReadOnlySpan<Byte>, ReadOnlySpan<Byte>, ReadOnlySpan<Byte>)` and
`IsSupported`. `MetadataUpdateHandlerAttribute` is assembly-targeted and `AllowMultiple`; the named
type implements `static void ClearCache(Type[]?)` and/or `static void UpdateApplication(Type[]?)`,
"visibility of the methods does not matter", and after an update the runtime calls every
`ClearCache` first, then every `UpdateApplication` -- "this enables applications to refresh the
application state, trigger a UI re-render, or other such reactions". That is precisely the mechanism
WinUI, WPF and Blazor use, and it is the reason B gives a hot reload where A gives an IL swap.

*Unverified (my own knowledge):* that `DOTNET_MODIFIABLE_ASSEMBLIES=debug` must be present in the
target's environment **at process start** for `MetadataUpdater.IsSupported` to be true. The docs
page for the class does not state it; the variable's presence in the watcher assembly is consistent
with it. The spike should assert `IsSupported` in the probe rather than trust this.

**What it costs.** Both `DOTNET_MODIFIABLE_ASSEMBLIES` and `DOTNET_STARTUP_HOOKS` are read at
process start, so B is launch-only too. The agent is new machinery RoseMCP has no precedent for --
and it is a managed assembly loaded into someone else's process, so it is per-TFM and effectively
per-RID, the same constraint `RoseMcp.LiveApp.csproj:9-15` already lives under. The pipe protocol is
ours to invent, since the SDK's is internal and undocumented. Against that: **no debugger needed**,
the app never stops, and the framework refreshes itself.

### Recommendation

**Do A's apply first and B's second, and build one delta half that both consume.** Concretely:

- The *emit* half is identical for both. `EmitDifference` produces metadata, IL and PDB deltas as
  byte arrays; `ICorDebugModule2::ApplyChanges` takes (metadata, IL) and `MetadataUpdater.ApplyUpdate`
  takes (metadata, IL, PDB). One `CodeDelta { ModulePath, Metadata, Il, Pdb, UpdatedTypes }` record
  in Contracts serves both. **Nothing about the worker side is architecture-specific**, which is what
  makes doing A first cheap rather than throwaway.
- **A ships first** because it is the smaller delta against what exists: a module-handle dictionary,
  one flag call in a callback already handled, one `ApplyChanges`, and a stop RoseMCP already does.
  It proves the whole pipeline end to end against `DebugProbeTarget` inside one milestone, which is
  what makes the rest estimable.
- **B is what makes it a product.** The common agentic loop is "run the app, edit, see it" with no
  debugger in the way, and without `MetadataUpdateHandler`s firing, a WinUI hot reload does not
  visibly reload. Ship it second, over the same delta half.

**The XAML tap is unaffected either way.** It is injected by the framework's own
`InitializeXamlDiagnosticsEx` (`XamlProviderSession.Inject`), not by the debugger and not by a
startup hook, so `rose_xaml_*` keeps working under B with no debugger attached. That is an argument
*for* B beyond C# hot reload: today the whole XAML live-edit feature is gated behind attaching a
debugger it does not need.

## Delta computation and the Features decision

The split, restated precisely. **The emit half is public** in the pinned
`Microsoft.CodeAnalysis` 5.9.0: `Compilation.EmitDifference` (four overloads),
`EmitBaseline.CreateInitialBaseline`, `SemanticEdit` (02's facts, read from the package's own XML
docs). **The analysis half is not**: turning two syntax trees into an ordered `SemanticEdit` list,
and deciding which changes are rude edits, lives in `AbstractEditAndContinueAnalyzer` / `EditSession`,
`internal` to `Microsoft.CodeAnalysis.Features`.

Three things I verified that change the shape of the options, and that WRK-22 could not have known:

1. **The Watch external-access shim moved, and got narrower.** In the local SDK (10.0.302),
   `Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll` is assembly version **5.6.0.0** and defines
   exactly two types: `HotReloadService` and `HotReloadMSBuildWorkspace`, both in
   `Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api`. `HotReloadService` carries
   `StartSessionAsync`, `GetUpdatesAsync`, `CommitUpdate`, `DiscardUpdate`, `EndSession`,
   `CapabilitiesChanged`, `WithProjectInfo`, `GetTargetFramework`, a nested `DebuggerService`, and
   nested `Update`, `Updates`, `Status`, `RunningProjectInfo`. Confirmed on the web against
   `dotnet/roslyn`'s `src/Features/ExternalAccess/HotReload/Api/HotReloadService.cs` -- the class
   line is `internal sealed class HotReloadService`, the constructor is
   `HotReloadService(SolutionServices, Func<ValueTask<ImmutableArray<string>>> capabilitiesProvider)`,
   `Status` is `NoChangesToApply | ReadyToApply | Blocked` -- and against PR dotnet/roslyn#80556,
   "Move Watch EA to a separate assembly Microsoft.CodeAnalysis.ExternalAccess.HotReload".
   `WatchHotReloadService` no longer exists: the string does not appear in
   `Microsoft.CodeAnalysis.Features.dll` 5.6 at all.
2. **Both types are `internal`, behind a strong-named `InternalsVisibleTo`.** Verified by reading the
   assembly's own metadata: the type's visibility flag is `NotPublic`, and the assembly carries
   exactly three IVT grants -- `Microsoft.DotNet.HotReload.Watch`,
   `Microsoft.DotNet.HotReload.Utils.Generator` and
   `Microsoft.CodeAnalysis.ExternalAccess.HotReload.UnitTests` -- each with a public key, so no
   assembly RoseMCP can build satisfies one. Confirmed on the web: the discussion around #80556
   records that `Microsoft.DotNet.HotReload.Utils.Generator` "is currently using Reflection to invoke
   the APIs", which is Microsoft describing the only route a non-grantee has.
3. **`Microsoft.CodeAnalysis.ExternalAccess.HotReload` is not published on nuget.org** (the package
   page 404s). `Microsoft.CodeAnalysis.Features` *is*, and 5.9.0 exists -- nuget.org's flat index
   lists `4.13.0, 4.14.0, 5.0.0-2.final, 5.0.0, 5.3.0-2.final, 5.3.0, 5.6.0, 5.9.0`.
   **Not verified:** whether `Microsoft.CodeAnalysis.Features` 5.9.0 still carries an ExternalAccess
   Watch/HotReload entry point at all. The package is not in the local cache (`D:\NuGet` holds
   `microsoft.codeanalysis.{analyzers,common,csharp,csharp.workspaces,workspaces.common,workspaces.msbuild}`
   and no `.features`), and I did not download it. Given #80556 moved the shim *out* of Features and
   the 5.6 SDK copy shows it gone from Features, the working assumption is that a
   `Microsoft.CodeAnalysis.Features` reference **does not** get you the shim. **This is the first
   thing the spike must check, because it decides between options (i) and (ii).**

### Option (i): reference `Microsoft.CodeAnalysis.Features` and use the ExternalAccess shim

WRK-22 is right that `no-roslyn-features-dependency` argues against the wrong thing -- reason 3 is
factually wrong (`CodeFixCatalog` reflects over the *project's* analyzer references,
`CodeFixCatalog.cs:59-64`, so nothing would register), and reason 2's version coupling is already
paid by `RoseMcp.XamlStubs` pinning 5.9.0 exactly. But a wrongly-argued decision does not make this
option cleanly available: the shim assembly is not on nuget.org, its types are internal, and the IVT
names three Microsoft assemblies. The realistic shapes are:

- **(i-a) Reference Features and reach the internal service by reflection** -- exactly what
  Microsoft's own `HotReload.Utils.Generator` does. Legal, unsupported, and brittle in the specific
  way that hurts: a signature change is a `MissingMethodException` at run time, not a build break.
  Mitigated by binding every member once at session start and refusing the feature by name if any is
  missing.
- **(i-b) Reference Features and reimplement the shim over `IEditAndContinueService`** -- the shim is
  thin, but everything it wraps is internal too, so this is (i-a) with more reflection and more
  surface.

Either way the Features reference itself is now *cheap and justified*: the decision's real objection
(Features' services are internal, so the reference buys little) is exactly reversed here, because the
thing we want is internal-but-reachable and has no public substitute at any price.

### Option (ii): load the SDK's own dotnet-watch assemblies at run time

The worker already locates an SDK (`Microsoft.Build.Locator`) and loads MSBuild out of it at run
time, with `ExcludeAssets="runtime"` on the compile-time references (`Directory.Packages.props`,
"MSBuild evaluation (worker)"). Loading
`{sdk}/DotnetTools/dotnet-watch/{ver}/tools/net10.0/any/Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll`
and its `Microsoft.CodeAnalysis.Features.dll` is the same trick with the same justification, and the
brief is right to ask whether it is any worse.

**It is, meaningfully.** MSBuild's run-time surface is a stable, public, documented API that the
locator exists to serve; this is an internal type in an assembly the SDK ships for one consumer.
Worse, it is a **Roslyn version collision**: the SDK folder carries Roslyn **5.6.0.0** while the
worker pins **5.9.0**, and the `Solution` handed to `HotReloadService.StartSessionAsync` must be
*that* Roslyn's `Solution`. So option (ii) means either loading a second, older Roslyn into the
worker in its own `AssemblyLoadContext` and re-creating the whole solution inside it, or pinning the
worker to whatever Roslyn the machine's SDK happens to ship -- which changes under you on every SDK
update, on a machine you do not control. That is not "the same as MSBuild"; that is a second Roslyn
and a second solution load.

### Option (iii): write the EnC analyzer ourselves

No, and the reason is this repository's own standard rather than effort. Rude-edit classification is
hundreds of rules over the whole C# grammar plus every generated-code interaction, and getting one
wrong does not fail -- it emits a delta the runtime accepts, and the process then behaves
incorrectly. That is the confidently-wrong-answer failure class the invariants exist to prevent,
reproduced at the largest scale in the codebase. `SemanticEdit` ordering and active-statement mapping
are the same story. Roslyn's implementation is the product of years of shipping it in Visual Studio.

### Recommendation

**(i-a): take the `Microsoft.CodeAnalysis.Features` 5.9.0 reference and reach `HotReloadService` by
reflection behind one sealed adapter in the worker** -- with the spike (M1) confirming first that
5.9.0 carries the shim. If it does not, the choice collapses to (ii) with its second-Roslyn problem,
and the whole estimate moves by weeks; that is why the spike is milestone one and not a footnote.

Amend `no-roslyn-features-dependency` as WRK-22 suggests, and add the fourth point: *hot reload is
the evidence that changes the answer, and what it buys is an internal API with no public substitute,
reached by reflection behind one adapter whose failure mode is a startup probe rather than a wrong
answer.* The adapter binds every member it needs at session start and refuses the whole feature with
a named reason if any is missing -- "an error says what went wrong, not that something did" applies
with special force to a reflection boundary, where the default failure is a `MissingMethodException`
from inside a tool call.

## Where each piece lives

The process split RoseMCP already has is the right one, and it falls out of the architecture rather
than being imposed on it: the worker has Roslyn and no debugger, the host has a debugger and no
Roslyn. Hot reload is the first feature that needs both, which is why it is the first feature whose
design is mostly about the seam.

### Worker: baseline and emit

- **A `HotReloadSession` object, one per (workspace, target) pair**, holding the pinned baseline
  `Solution`, the `Revision` it was pinned at, and one `EmitBaseline` per project that has a module in
  the target. Pinning costs almost nothing: a `Solution` is immutable and shares syntax trees with its
  successors (02's facts), so the only cost is keeping older trees alive.
- **`BuildFreshness` is the precondition, structurally.** `EmitBaseline.CreateInitialBaseline` must be
  given the assembly the process is actually running. `BuildFreshness.Of(solution, project, ct)` with
  `Stale == false` is exactly that question, already answered without a build
  (`BuildFreshness.cs:24-96`). Starting a session refuses per project with the freshness verdict as
  the reason -- which is a better error than anything a new code path would invent, because the text
  already explains the failure ("Build before running anything from it").
- **Revision is the baseline identity, and it has to become per-project.** The session records the
  `_revision` it pinned at; the barrier's snapshot carries the current one. Per-project, keep a pair:
  `Project.GetDependentSemanticVersionAsync` and the project's latest document version. **Corrected by
  #299** -- the first alone does not move for a body-only change, which is the edit hot reload exists
  to apply, so keying on it would skip emitting for exactly those. Unchanged in both means "no delta
  for this project", which is what keeps a 50-project solution from emitting 50 times per edit.
- **Emit is a read, and must stay off the pump.** `EmitDifference` over the barrier snapshot takes no
  writer; committing the advanced `EmitBaseline` does. That is WRK-10's `Prepare`/`Commit` shape
  arriving for a second reason, and it is the shape that makes "emit does not block every
  `rose_symbol_info`" true rather than hoped for.
- **The generated-document question is settled by the design already.** `XamlStubGenerator` combines
  with `CompilationProvider` and emits deterministically (`XamlStubGenerator.cs:38`), so unchanged
  markup diffs to nothing. EnC treats generated documents as source, which means a XAML edit
  legitimately produces a C# delta -- and that, not the live-tree patch, is the real "XAML hot
  reload" this repository has been careful not to claim.

### Broker: orchestration, and the join nobody has made

- **The join is the interesting part.** `LiveAppSessionManager` says outright it is "the debugging
  counterpart to `WorkspaceManager`... a session is per running target, whereas a worker is per
  solution, so the two are tracked separately" (`LiveAppSessionManager.cs:10-18`). Nothing joins them.
  What joins them is the **output path**: `BuildFreshness` knows `Project.OutputFilePath` for every
  project, and `TargetSymbols.Remember` sees the file of every module the target loads
  (`TargetSymbols.cs:44-54`). Intersect the two sets and you have the project-to-module map a
  hot-reload session *is*. That is a real computation, not a guess, and it also answers "which of my
  open workspaces is this app?" -- a question the broker cannot answer today.
- **Tool surface: `rose_hot_reload_start` / `rose_hot_reload_apply` / `rose_hot_reload_end`, and not
  automatic.** The argument for automatic (every `rose_*` write and every disk change the barrier
  absorbs pushes a delta) is that it is the loop people want. Three reasons it is wrong here, and all
  three are already written down in this repository:
  1. `a-live-edit-is-diffed-against-what-was-last-sent` argues exactly this for XAML: "Applying on
     every save would change a running app from a keystroke, including the saves mid-edit that do not
     parse, and an MCP tool has nowhere to push the outcome. The agent asks, and is told." Every word
     transfers. A rude edit discovered on an automatic apply has no caller to tell.
  2. A code delta is *less* reversible than a XAML property set, not more: an `EmitBaseline` advances
     and the process's metadata grows. There is no undo.
  3. It would make every write tool's latency include an emit and an apply, and the failure would be
     attributed to the write.
  So: explicit, with `rose_hot_reload_apply` taking a session id and optionally a project filter, and
  the write tools' descriptions pointing at it. Revisit automatic only with dogfooding records showing
  agents forgetting to call it.
- **Attribution rides the existing rail.** "Every result carries a `revision` and names the workspace
  that answered", added once in `WorkspaceManager` -- an apply result should carry the *baseline*
  revision and the revision it advanced to, which is what makes "I applied edits you have since
  overwritten" a statement rather than a mystery.

### Host: environment, module registry, apply

- **Environment at launch** (`RuntimeAttachment.cs:81`): `lpEnvironment` stops being `IntPtr.Zero` and
  becomes a merged block -- the host's own environment plus what the session asks for. UWP already has
  the plumbing (`Uwp.cs:106-133`); the exe path needs the same multi-string built by hand.
- **Module registry**: `Dictionary<string, CorDebugModule>` filled in `TargetSymbols.Remember`, which LIV-05
  wants anyway to stop `AddBinding` async-breaking the whole target per breakpoint. One change, two
  features.
- **JIT flags inside the `LoadModule` callback**, because there is nowhere else they can go, gated on
  whether this session was started with hot reload armed -- `CORDEBUG_JIT_DISABLE_OPTIMIZATION` on
  every module is a real performance cost nobody asked for.
- **Apply with the target synchronised**, following the stop/continue pair `TargetSymbols.Walk` sets out (`TargetSymbols.cs:65`), on the same
  `_gate` and callback discipline `BindAgainstLoadedModules` already runs under.
- **PDB deltas into `RoseMcp.Symbols`**: this is the one host-side piece with no shortcut. The cache
  models disk (`SymbolCache.cs:33-56`, stamp-based); a debugged process needs *the module as loaded,
  plus an ordered list of deltas*. Until that exists, every line number and local name after the first
  apply describes the wrong build, confidently.
- **Remap**: `Record` grows two arms for `FunctionRemapOpportunity` and `FunctionRemapComplete`. The
  simple correct v1 is to answer every opportunity by remapping to the same IL offset when the method
  is unchanged in that region and to refuse the edit for a method with an active frame otherwise --
  and to *say* that, rather than silently applying an edit that will not take effect until the frame
  unwinds.

### Contracts: `LiveCodeApplyResult`

Modelled on `LiveXamlApplyResult` (`Applied`, `Total`, `Results`, `Notes`, `Detail`) with
`LiveCodeEditResult` per changed member: the symbol as named, the project, the status (`applied`,
`rude edit`, `no change`, `active frame`), and the rude-edit diagnostic id and message where there is
one. The XAML pair is the right precedent and the right prose contract: `Detail` set with no results
means the apply could not run at all, `Notes` carries what was worked out but not applied, and a
failure is not retried by the next apply. The last clause is load-bearing for the same reason it is
in XAML and a stronger one: an `EmitBaseline` has advanced.

`CodeDelta` is the wire record, and it is the one place bytes cross a process boundary. See HOT-08.

## Strengths

The things a hot-reload epic must not refactor away.

- **`BuildFreshness` is the initial-baseline precondition, already written and already exposed.**
  `EmitBaseline.CreateInitialBaseline` is only valid against the assembly the process is running, and
  `BuildFreshness.Of` answers that per project off the design-time build with no build of its own
  (`BuildFreshness.cs:24-96`). Its verdict text is better than anything a new code path would write.
- **The XAML live edit is a complete, shipped rehearsal of the apply.** `XamlApplyBaseline`
  (`src/RoseMcp.XamlDiff/XamlApplyBaseline.cs`) is baseline-of-what-was-sent with an explicit
  first-apply-records-nothing rule and a baseline that advances on partial failure *because edits are
  not idempotent* -- the same three properties an `EmitBaseline` chain has, argued from the same
  premise. `XamlApply.ApplyEditsCore` is compute-outside, send-a-batch,
  per-edit-outcomes. `LiveXamlApplyResult` / `LiveXamlEditResult` are the result shape. None of this
  is reusable code and all of it is reusable design.
- **The module-load callback is handled, at the one moment EnC can be armed.** `Record` ->
  `LoadModuleCorDebugManagedCallbackEventArgs` (`CorDebugSession.cs:750`) runs with the target
  stopped, which is where `SetJITCompilerFlags` must be called and where a baseline can be read
  safely.
- **The worker holds one immutable `Solution` and obtains compilations on demand.** Retaining a
  baseline solution is therefore a field, not a memory strategy.
- **The process split is already the one hot reload wants.** Roslyn in the worker, ICorDebug in the
  host, neither referencing the other. The thing that looks like an obstacle is the correct
  factoring; only the channel is missing.
- **`RuntimeFlavour.Describe` already tells CoreCLR from desktop CLR from .NET Native**
  (`Debugging/RuntimeFlavour.cs:42-72`), which is exactly the check that refuses hot reload on .NET
  Native rather than failing obscurely inside it.
- **`DebugProbeTarget` is a real target built to be debugged**, with `NoInlining`/`NoOptimization`
  where the tests need them (`tests/DebugProbeTarget/Program.cs:62-64`). A method edited while it
  loops is the probe this whole epic is testable against, and it exists.

## Findings

### HOT-01 `TargetSymbols.Remember` drops the `ICorDebugModule`, and it is the only place EnC can be armed
- **Severity:** High
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/TargetSymbols.cs:44-54`, called from
  `TargetBreakpoints.BindModule` `:1345`, reached from `Record` `:1061-1064`
- **What:** `Remember` takes a `CorDebugModule`, extracts its path into the registry and lets the
  object go. Both EnC entry points need that object and nothing else will do:
  `ICorDebugModule2::ApplyChanges` is a method on it, and `SetJITCompilerFlags` is documented as
  callable *only from inside the `LoadModule` callback for that module* -- which is the call stack
  `Remember` is standing in.
- **Why it matters:** It is not that the handle is inconvenient to get later; it is that for the JIT
  flag there is no later. Every module whose `LoadModule` callback has been delivered without the flag
  set is permanently ineligible for debugger-driven EnC for the life of the process. A session that
  forgets to arm a module cannot be repaired, only restarted.
- **Suggested change:** `Dictionary<string, CorDebugModule>` alongside the paths in `TargetSymbols`,
  filled in `Remember`, and an armed-at-load decision taken from session state. LIV-05 already asks for
  the same map so `AddBinding` stops async-breaking the whole target per breakpoint, so this is one
  change buying two features. Make the arming a property of the *session*, decided once at start, so no
  code path can reach `LoadModule` undecided. PR #265 made this cheaper: the registry is now a class of
  its own rather than a field on the session, so the map has an obvious home.

### HOT-02 The exe launch hands the target `IntPtr.Zero` for its environment
- **Severity:** High
- **Effort:** S (exe), S (UWP)
- **Where:** `src/RoseMcp.LiveApp/Debugging/RuntimeAttachment.cs:81`; contrast `Uwp.cs:106`, `:118-133`
- **What:** `CreateProcessForLaunch(commandLine, bSuspendProcess: true, IntPtr.Zero, workingDirectory)`
  -- the third argument is `lpEnvironment`, so the target inherits the broker's environment verbatim.
  The UWP path already passes a real block (`ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1\0\0`) through
  `IPackageDebugSettings::EnableDebugging`, and adding a second variable there is one string.
- **Why it matters:** `DOTNET_MODIFIABLE_ASSEMBLIES` and `DOTNET_STARTUP_HOOKS` are read at process
  start. Architecture B is not merely unimplemented on the exe path -- it is unreachable. Setting the
  variables on the broker instead would leak them to every target the broker ever launches, which is
  the wrong grain and would silently change how unrelated apps run.
- **Suggested change:** One `TargetEnvironment` type that produces the double-null-terminated block
  for both call sites, taking "the host's own environment, plus these". Both launch paths go through
  it, so the UWP special case stops being a special case, and the same type is where a future
  `ASPNETCORE_*` or `DOTNET_gcServer` override would go.

### HOT-03 The revision counter is per solution, so "this project has not changed" is not expressible
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Worker/WorkspaceSession.cs:146`, `:284`, `:389` (the single `_revision`);
  `DiagnosticsService.cs:104` (the per-project key that already exists)
- **What:** Every mutation and every reconciled disk change advances one counter for the whole
  solution. A hot-reload session needs to ask, per project, "is this project's compilation different
  from the one my `EmitBaseline` came from" -- and the answer it can get today is "something in the
  solution changed".
- **Why it matters:** Without it, an apply either emits for every project in the solution on every
  call (a 50-project solution pays 50 `EmitDifference`s for a one-line edit) or guesses.
  `Project.GetDependentSemanticVersionAsync` is Roslyn's own answer and is transitive over project
  references, which is the half EnC needs for a changed dependency. **Corrected by #299:** it is not
  sufficient on its own -- it does not move for a change confined to a method body, so a key built on
  it alone would decide "no delta" for the edit hot reload most exists to apply. The diagnostics
  cache had to pair it with the project's latest document version, and so does this.
- **Suggested change:** Record the per-project stamps -- both of them, per #299 -- alongside the
  solution revision in the session's baseline, and make the emit loop skip a project where neither
  has moved. Keep the solution revision as the *result* attribution ("what I emitted against"), which is what the
  everywhere-rule about revisions already wants.

### HOT-04 `SymbolCache` models disk, so after the first apply every symbol answer describes the wrong build
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Symbols/SymbolCache.cs:33-56` (stamp-based invalidation),
  `ModuleSymbols.cs:94-109` (PDB identity against the file), `PortablePdb.cs:121-186`
- **What:** The cache's identity for a module is the file on disk and its stamp. A debugged process's
  identity is *the module as it was loaded, plus every delta applied since*. These are the same thing
  only until something changes. Today the divergence is a rebuild (03 records it: the cache re-reads
  the new file while the debuggee still runs the old IL, and `PdbState.Mismatched` fires only on a
  disk-versus-disk mismatch). Hot reload makes the divergence routine and intentional.
- **Why it matters:** This is the confidently-wrong-answer failure the invariants are written against,
  aimed at the debugger: after one apply, `rose_debug_events`, frame lines, local names and
  `ReadMethodSource` all report against a build the process is not running, with no signal that
  anything is off. It is worse than a failure, and it is worse after hot reload than before because
  the user will have *asked* for the divergence.
- **Suggested change:** Give `RoseMcp.Symbols` a `ProcessModuleSymbols` keyed by (module, generation)
  that starts from the loaded baseline and is advanced by each applied PDB delta, with the disk-backed
  `SymbolCache` becoming generation 0. Every debugger-side read goes through the process view;
  `rose_symbol_info` and the worker keep the disk view. The type boundary is what stops a later reader
  picking the wrong one.

### HOT-05 A mutation runs on the single writer, so an emit would block every read
- **Severity:** Medium
- **Effort:** M
- **Where:** `WorkspaceSession.MutateAsync:135-162`; see WRK-10
- **What:** WRK-10 already records that a mutation runs locate, rewrite, format, write *and* compile
  on the pump. An `EmitDifference` for a multi-project solution is the largest such operation anyone
  would add, and the natural place to put it is beside the write that caused it.
- **Why it matters:** In http mode with the tray and an agent both connected, an apply would stall
  every navigation call for the duration of an emit -- and the emit is a read, so nothing about it
  needs the writer.
- **Suggested change:** Land WRK-10's `Prepare(snapshot) -> (after, T)` off-pump plus a short on-pump
  `Commit(after, expectedRevision)` *before* the hot-reload work, and make the emit a `Prepare`. The
  only thing the commit does is swap the advanced `EmitBaseline` in under the revision check, which is
  the compare-and-swap WRK-10 describes.

### HOT-06 The stop state machine has no room for "applying"
- **Severity:** High
- **Effort:** S
- **Where:** `src/RoseMcp.LiveApp/Debugging/TargetExecution.cs`, `StopRecord.cs`
- **Half done, #265** (LIV-02): what the target is doing is one value, and every guard is a pattern
  match.
- **What is left:** the `Applying(ApplyRecord)` arm. An apply is a state that is *neither* running nor
  stopped-at-a-breakpoint: synchronised deliberately, by us, for a bounded operation that must not be
  interrupted by the safety timer, a detach, or a second apply.
- **Why it matters:** The window an apply opens is the worst kind -- a detach or auto-continue firing
  between `ApplyChanges` and the baseline advancing leaves the process's metadata ahead of the worker's
  `EmitBaseline`, which no later apply can reconcile, because the deltas are ordered and there is no
  undo. Adding the arm is what makes the compiler enumerate every site that has to decide what
  "applying" means, instead of a reviewer doing it.
- **Suggested change:** Add the arm, and give `ApplyRecord` the same shape as `StopRecord`: it owns the
  bound on how long an apply may hold the target, and disposing it is what ends the state.

### HOT-07 Attach-to-running can never hot reload, and nothing says so
- **Severity:** Medium
- **Effort:** S
- **Where:** `RuntimeAttachment.cs:52-70` (`Attach`), `TargetBreakpoints.cs:153` (`BindAgainstLoadedModules`),
  `Tools/LiveAppDebugTools.cs` (`rose_debug_attach`)
- **What:** `SetJITCompilerFlags` is callable only inside `LoadModule` for that module (verified on
  learn.microsoft.com). For a process attached after it started, every already-loaded module's
  callback is in the past. `rose_debug_attach` therefore produces a session that can debug but can
  never apply a code change -- and an agent has no way to know that except by trying.
- **Why it matters:** "Agentic citizenship" here means the difference between a tool that refuses with
  a reason and one that fails at the end of a long loop. It also shapes the product: the useful
  RoseMCP hot-reload session is one RoseMCP *started*, which is a thing to say in the description of
  `rose_debug_launch` rather than discover.
- **Suggested change:** Make it a property of the session, reported by `rose_debug_list` and by the
  session summary (`CanHotReload`, with the reason when false: attached rather than launched, .NET
  Native, module loaded before arming). `rose_hot_reload_start` refuses with that reason. The
  framework's own answer for the modules loaded *after* an attach can still be yes, so the property is
  per module underneath and per session at the surface.

### HOT-08 There is no channel for bytes between the worker and the host
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceWorker.cs` and `src/RoseMcp.Broker/LiveAppSession.cs` (both
  MCP over stdio); `XamlApply.ApplyEditsCore` for what text transport looks like
- **What:** Everything crossing a RoseMCP process boundary today is text: JSON over stdio to the
  worker, JSON over stdio to the host, newline-framed commands to the tap. A metadata+IL+PDB delta is
  three byte arrays per changed project, and the only shape available without new machinery is base64
  inside a tool argument, routed worker -> broker -> host through two JSON hops.
- **Why it matters:** Base64 through two JSON serialisations is roughly 1.8x the bytes and two full
  copies per hop, and it puts binary payloads in the activity log and the tool transcript, where an
  agent may echo them. It is *survivable* for a v1 (a method-body delta is kilobytes) and unpleasant
  by the time somebody edits a large file. The real risk is that it gets designed by accident: the
  first implementation reaches for a string parameter because that is what is there.
- **Suggested change:** Decide it deliberately. Base64 in a tool argument for v1, with the decision
  recorded and a size cap that refuses rather than truncates; a side channel (a temp file the host
  reads and deletes, which needs no new protocol and no new port) the moment a delta exceeds it. Do
  not invent a second socket for this -- `06-ipc-and-protocols.md` is already counting twelve
  boundaries.

### HOT-09 Nothing pairs a workspace with a live-app session, and the key already exists
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/LiveAppSessionManager.cs:10-18`, `src/RoseMcp.Broker/WorkspaceManager.cs`
- **What:** The two registries are deliberately unrelated -- "a session is per running target, whereas
  a worker is per solution, so the two are tracked separately". A hot-reload session is precisely a
  pairing of the two, and the join key exists on both sides already: `Project.OutputFilePath` from the
  design-time build (`BuildFreshness.cs:40`, `ProjectGraphService.cs:59`) and the module file
  `TargetSymbols.Remember` sees (`TargetSymbols.cs:44-54`).
- **Why it matters:** Without the join the caller has to supply both ids on every call and can pair
  them wrongly with no diagnostic -- applying a delta built from solution A to a process running
  solution B is an apply that *succeeds* and produces a process that is neither.
- **Suggested change:** A `HotReloadPairing` in the broker that computes the project-to-module map by
  intersecting output paths with loaded module paths, refuses a pairing with no intersection, and
  reports the map in the start result so the caller can see what it got. It also gives the broker an
  answer to "which workspace is this app?", which nothing can answer today.

### HOT-10 `BuildFreshness` counts `obj/` artefacts as sources, so the baseline precondition can flap
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Worker/BuildFreshness.cs:134-145` (`Sources`, which yields every
  `AnalyzerConfigDocument`)
- **What:** Observed live: `rose_build_freshness` on `RoseMcp.LiveApp` in this worktree reports
  `newestSourcePath` = `obj\Debug\net10.0-windows\RoseMcp.LiveApp.GeneratedMSBuildEditorConfig.editorconfig`.
  That file is written by MSBuild, not by a person, and a design-time build rewrites it. So "a source
  is newer than the output" can become true because RoseMCP itself re-evaluated the project.
- **Why it matters:** As a freshness hint it is a false "you need to build". As the *precondition for
  capturing an EnC baseline* it is worse: the session refuses to start, or -- if the check is
  inverted later for convenience -- accepts a baseline whose real staleness it never measured. The
  wrong answer is confident either way, and the verdict text names a file the user has never edited,
  which reads as a bug in Rose rather than a fact about the build.
- **Suggested change:** Exclude documents under the project's intermediate output path from `Sources`,
  or restrict the analyzer-config scan to `.editorconfig` files that are not MSBuild-generated. The
  project file itself stays, for the reason the existing comment gives.

### HOT-11 Ending a session after an applied edit is already classified and not implemented
- **Severity:** Low
- **Effort:** S
- **Where:** `DetachProtocol.cs:106` (`CORDBG_E_DETACH_FAILED_ON_ENC` in `IsRefusal`), `CorDebugSession.ReleaseForDetach`
  (`ReleaseForDetach`)
- **What:** The one EnC symbol anywhere in `src` is the HRESULT for "cannot detach, this process has
  had an edit applied", and it is already treated as a refusal rather than retried -- which is
  correct, and means the detach path will start refusing the moment hot reload works.
- **Why it matters:** An agent that applies one edit and then tries to detach gets a refusal whose
  explanation nobody has written, and the session becomes un-endable except by killing the target.
- **Suggested change:** `rose_hot_reload_end` (and `rose_debug_detach`) states the rule plainly: a
  process that has had code applied cannot be released back to running un-debugged, so ending the
  session ends the process. Say it in the start result too, so the caller consents before the first
  apply rather than discovering it after.

### HOT-12 `no-roslyn-features-dependency` is the stated blocker and it does not survive contact
- **Severity:** Medium
- **Effort:** S (the decision), L (what it unblocks)
- **Where:** `docs/decisions/no-roslyn-features-dependency.md`; see WRK-22 for the argument
- **What:** WRK-22 shows the decision's reason 3 is factually wrong and reason 2 is a cost already
  paid. This review adds the part that decides it: the analysis half of EnC has no public substitute
  at any price, and the shim that reaches it (`Microsoft.CodeAnalysis.ExternalAccess.HotReload`) is
  internal, is not on nuget.org, and grants `InternalsVisibleTo` to three Microsoft assemblies --
  Microsoft's own `HotReload.Utils.Generator` reaches it by reflection.
- **Why it matters:** The decision as written is the reason nobody has started, and its reasons are
  not the real ones. Left standing it will be re-litigated at the worst moment, in the middle of the
  epic.
- **Suggested change:** Amend it now, before any code: add "what changes the answer: hot reload", name
  the shim, record that the route is reflection behind one adapter, and state the mitigation (bind
  every member at session start, refuse by name if any is missing). A decision that says "yes, for
  this, in this shape" is worth more than one that is quietly ignored.

## Milestones

Each is independently shippable and independently testable. Two prerequisites from other reviews are
listed first because hot reload makes them load-bearing rather than tidy.

### ~~P1 -- LIV-02: the target-execution union (M)~~
**#265.**

### P2 -- WRK-10: `Prepare` off-pump, `Commit` on it (M)
Also not hot-reload work. It is what makes an emit a read rather than a stall, and its
compare-and-swap on the revision is what makes an `EmitBaseline` swap safe. **Proof:** existing test
plus one asserting a read completes while a verifying `rose_change_signature` is in flight.

### M1 -- Spike: can we reach the EnC analysis API at all? (S, 2-3 days) -- **do this first**
A throwaway console app, outside the solution. Reference `Microsoft.CodeAnalysis.Features` 5.9.0 from
nuget.org and answer, in order: (a) does 5.9.0 contain an ExternalAccess hot-reload or Watch entry
point at all, or did #80556 leave Features without one; (b) if so, can it be driven by reflection --
construct it, `StartSessionAsync`, change a document, `GetUpdatesAsync`, get back a non-empty delta;
(c) what does `Status == Blocked` look like, and what do rude-edit diagnostics look like. If (a) is
no, the answer is option (ii) with its second-Roslyn problem, and everything after this moves by
weeks -- which is the entire reason this is milestone one.
**Proof:** a delta's metadata and IL byte arrays, non-empty, for a one-line method-body change in
`tests/fixtures`. Write the finding down either way; a negative result is the most valuable output
this epic can produce in week one.

### M2 -- A controlled environment and an armed module registry (M)
HOT-02 and HOT-01. `TargetEnvironment` for both launch paths; `Dictionary<string, CorDebugModule>` in
`TargetSymbols`; `SetJITCompilerFlags(CORDEBUG_JIT_ENABLE_ENC | CORDEBUG_JIT_DISABLE_OPTIMIZATION)`
inside `LoadModule` when the session was started armed; `SetDesiredNGENCompilerFlags` on the process.
`CanHotReload` on the session with its reason (HOT-07). Ships value on its own: LIV-05's per-breakpoint
full-target stop goes away with the same map.
**Proof:** launch `DebugProbeTarget` armed, evaluate `System.Reflection.Metadata.MetadataUpdater.IsSupported`
through `rose_debug_evaluate` and assert `true`; read the module's JIT flags back and assert EnC is
set; assert a session created by `rose_debug_attach` reports `CanHotReload = false` with the reason.

### M3 -- The freshness precondition, correctly (S)
HOT-10: stop counting `obj/` artefacts as sources. Ships on its own -- `rose_build_freshness` is a
tool people use today and it currently names a file nobody edited.
**Proof:** a unit test staging a project whose only "newer" file is a generated editorconfig, asserting
`Stale == false`.

### M4 -- Worker: pin a baseline, emit a delta (L)
`HotReloadSession` in the worker: pinned `Solution` + revision, per-project `VersionStamp` (HOT-03),
`ModuleMetadata.CreateFromFile` + `CreateInitialBaseline` per project gated on `BuildFreshness`, and
`GetUpdatesAsync` behind the reflection adapter from M1 with every member bound and verified at start.
No transport, no target: the output is a `CodeDelta` in memory.
**Proof:** integration test against a fixture solution -- open, emit (expect nothing), edit a method
body through `rose_replace_body`, emit again, assert one project produced non-empty metadata and IL;
edit a method signature, assert a rude-edit diagnostic with a message.

### M5 -- Contracts and transport (M)
`CodeDelta`, `LiveCodeApplyResult`, `LiveCodeEditResult`; `rose_hot_reload_start` / `_apply` / `_end`;
the broker's `HotReloadPairing` joining output paths to loaded modules (HOT-09); base64 with a size
cap and a recorded decision (HOT-08).
**Proof:** the delta reaches the host and is byte-identical to what the worker emitted; a pairing with
no intersecting module refuses and names both sides.

### M6 -- Apply through ICorDebug (M) -- **the probe milestone**
`ApplyChanges` on the armed module with the target synchronised, under an `Applying` arm added to the
union P1 built (HOT-06).
**Proof:** the one that matters. Launch `DebugProbeTarget`, let it loop, use `rose_replace_body` to
change what `Inspect` computes, apply, and assert through `rose_debug_evaluate` at a breakpoint that
the new value is observed -- with no relaunch and no rebuild. That single test is the definition of
done for architecture A.

### M7 -- Symbols that model the process (M)
HOT-04: `ProcessModuleSymbols` keyed by (module, generation), advanced by each PDB delta; debugger
reads go through it.
**Proof:** after an apply that moves a method's lines, a breakpoint set by line binds to the right IL
offset and a local reports the right name -- both currently wrong the moment a delta lands.

### M8 -- Result quality and refusals (M)
Rude-edit diagnostics surfaced per edit; `CORDBG_E_DETACH_FAILED_ON_ENC` explained (HOT-11); the
"baseline advances even on partial failure, and a failure is not retried" contract written into the
tool description the way `rose_xaml_apply`'s already is.
**Proof:** a rude edit is refused with the Roslyn diagnostic and the process is unchanged; detach
after an apply refuses with prose rather than an HRESULT.

### M9 -- The agent: architecture B (L)
A `RoseMcp.HotReloadAgent` assembly, a startup hook, a pipe protocol, `MetadataUpdater.ApplyUpdate`,
per-TFM and per-RID build and staging beside the provider the tap already stages. This is where
`MetadataUpdateHandler`s start firing and a WinUI app visibly re-renders.
**Proof:** a WinUI 3 probe with a `[MetadataUpdateHandler]`-driven refresh shows the change on screen
with **no debugger attached**, and `rose_xaml_*` still works in the same session.

### M10 -- Active-frame remap (M)
`FunctionRemapOpportunity` / `FunctionRemapComplete` arms in `Record`, `ILFrame.RemapFunction`.
**Proof:** edit a method the probe is currently executing (a long loop), assert the remap is taken or
the edit is refused with "active frame" -- and never silently ignored.

### M11 -- Framework coverage (M)
UWP Debug (CoreCLR flavour) measured rather than assumed; WinUI 3 packaged; WPF; .NET Native refused
by name through `RuntimeFlavour.Describe`, which already has the prose.
**Proof:** the matrix in the next section, each row with a test or a written-down refusal.

### Distance

| Path | Milestones | Focused weeks |
|---|---|---|
| Prerequisites (P1, P2) | 2 | 1 - 1.5 |
| Architecture A, end to end (M1-M8) | 8 | 3.5 - 5 |
| Architecture B (M9) | 1 | 1.5 - 2 |
| Polish (M10, M11) | 2 | 1 - 1.5 |
| **Credible v1: plain .NET + WinUI 3 unpackaged, debugger-driven** | P1-M8 | **5 - 6.5** |
| **Both architectures, framework matrix covered** | all | **7 - 10** |

**The assumption, spelled out:** "focused week" means roughly this repository's demonstrated pace --
446 commits in 3.5 weeks, the whole debugger/tap/inspector stack built 2026-09-02 to 2026-09-04 -- with
a working ICorDebug session and a working probe already in hand, and with M1 coming back positive. If
M1 comes back negative (Features 5.9.0 has no reachable entry point), add **2-3 weeks** for the
second-Roslyn `AssemblyLoadContext` and a second solution load, and re-examine whether the feature is
worth it at all.

**Against the XAML live-edit epic.** Issues #11 and #12 ran from `bcda18c` (2026-09-02, "Productionise
the XAML diff engine") to `190aa1f` (2026-09-05, "Call it a live edit, not a hot reload") -- about a
week including the provider work around it. C# hot reload is **five to seven times that**, and the
reason is one thing: the XAML epic *consumed* an apply API the framework already provides
(`IVisualTreeService`, reached through `InitializeXamlDiagnosticsEx`), so all the work was in the diff
and the addressing. Here there is no provided apply. Both halves -- the analysis that produces the
delta and the channel that lands it -- have to be built, and one of them is reached by reflection
through an unsupported door. Anyone estimating this from the XAML epic's shape will be wrong by a
factor of five.

## Framework matrix

| Target | Architecture | Environment at start | What refreshes the UI |
|---|---|---|---|
| **Plain console / .NET app** | A now, B later | A: none needed. B: `DOTNET_MODIFIABLE_ASSEMBLIES=debug` + `DOTNET_STARTUP_HOOKS`, both blocked on HOT-02 | Nothing to refresh -- new IL runs on the next call. This is the honest v1 target and the probe. |
| **WinUI 3, unpackaged (Windows App SDK)** | A works; **B is the one that matters** | Same as above; launched as an ordinary exe, so HOT-02 is the whole gate | Under A: nothing. New IL, stale visual tree. Under B: the Windows App SDK's `[MetadataUpdateHandler]` types run and the framework re-renders. *Unverified -- I did not enumerate WinAppSDK 2.4.0's handler attributes; the spike in M9 should, since "WinUI supports hot reload" is a claim about that assembly's attributes and nothing else.* |
| **WinUI 3, packaged** | A and B, with a deployment problem | Activation goes through PLM, so the environment is `IPackageDebugSettings::EnableDebugging` as UWP already does (`Uwp.cs:118-133`). The agent assembly must live somewhere the package can load it -- the same staged-into-a-sandbox-folder problem the tap already solved, and the same ALL APPLICATION PACKAGES grant | Same as unpackaged |
| **UWP classic, Debug (UWP CoreCLR flavour)** | A, probably; B, measure | `Uwp.cs:106` already passes an environment block -- adding `DOTNET_MODIFIABLE_ASSEMBLIES=debug` is one string in a const. The resume stub (`UwpResumeStub.cs`, `UwpStartupCoordinator.cs`) already holds the app until the debugger has armed startup notification, so `LoadModule` is observed for every module | **Measure, do not assume.** This is a different CoreCLR flavour under PLM; whether it honours `CORDEBUG_JIT_ENABLE_ENC` and whether `MetadataUpdater.IsSupported` comes back true are empirical. `RuntimeFlavour.Describe` already distinguishes it |
| **UWP classic, Release (.NET Native)** | **Impossible. Say so.** | n/a | .NET Native is AOT with no CoreCLR in the process; there is no JIT to re-JIT, no ICorDebug session at all, and `MetadataUpdater` does not exist in that world. `RuntimeFlavour.Describe` (`RuntimeFlavour.cs:47-55`) already writes this refusal better than a new message would -- "there is no CoreCLR in it... ICorDebug cannot debug it at all. This is what a Release UWP build looks like." Hot reload's refusal should reuse that text verbatim |
| **WPF (.NET, not Framework)** | A and B | Ordinary exe, so HOT-02 again | WPF ships `[MetadataUpdateHandler]` types, so B refreshes. *From my own knowledge, unverified locally -- `Microsoft.WindowsDesktop.App.Ref` is installed on this machine and the M9 spike can confirm it in minutes.* Under A, nothing refreshes |
| **Desktop .NET Framework** | Neither | n/a | `RuntimeFlavour.Describe` already refuses: "it is hosting the desktop .NET Framework... this debugger attaches to CoreCLR". Out of scope and already explained |

Two things the matrix makes plain. First, **HOT-02 is on every row that matters** -- one `IntPtr.Zero`
is the single blocking defect for the whole of architecture B. Second, **architecture A is a good
answer only for console-shaped targets**: everywhere a UI is involved, the IL changes and nothing
redraws, which is exactly the gap `it-is-a-live-edit-not-a-hot-reload` was written to be honest
about. If the goal is "edit C# and see the app change", the destination is B, and A is the milestone
that proves the delta pipeline against a probe.

## Pit-of-success inversions

- **Rule today:** the module handle must be kept and the JIT flag set at exactly the `LoadModule`
  callback. **Mechanism:** an `ArmedModule` record created only inside the callback handler, holding
  the `CorDebugModule` and the flags actually set, and an `ApplyChanges` that takes an `ArmedModule`
  rather than a path -- so a module that was never armed has no value to pass.
- **Rule today:** a delta is emitted against the baseline solution, not the current one. **Mechanism:**
  `EmitDifference` reachable only through `HotReloadSession`, which owns the pinned `Solution` and
  never exposes it; the emit takes the *new* snapshot as its argument, so the direction cannot be
  written backwards.
- **Rule today:** the baseline advances whether or not every edit took. **Mechanism:** the same one
  XAML uses -- `Advance` is called from the one place that sends, not from the success branch. Copy
  `XamlApplyBaseline`'s shape and its remarks.
- **Rule today:** never apply a delta to a process that is not the one it was built for.
  **Mechanism:** `CodeDelta` carries the module's MVID, and the host compares it to the loaded
  module's before applying. A path comparison is not enough -- the same path can be two builds.
- **Rule today:** a reflection-bound API must fail at session start, not at the first apply.
  **Mechanism:** the adapter binds every `MethodInfo` in its constructor and throws a named refusal
  naming the missing member; there is no lazy lookup anywhere in it.
- **Rule today:** symbol reads against a debugged process must use the process's generation, not
  disk's. **Mechanism:** two types, not one flag -- `ProcessModuleSymbols` and `SymbolCache` -- with
  the debugger's call sites taking the former by parameter type.

## Open questions for Steve

1. **Which loop is the destination?** "Attach a debugger, stop the app, swap IL" and "run the app, edit,
   watch it re-render" are different products. Architecture A gives the first in ~5 weeks; the second
   needs B on top. If the answer is the second, M9 stops being polish and the ordering above may want
   inverting -- I recommended A-first for pipeline-proving reasons, not product reasons.
2. **Is an unsupported reflection dependency on Roslyn internals acceptable here?** It is what
   Microsoft's own generator does, and there is no alternative that is not worse. But it is the first
   thing in this repository that can break on a package bump with no build error, and it deserves a
   yes or no before M1 rather than after.
3. **Does hot reload imply a build?** `EmitBaseline.CreateInitialBaseline` needs the assembly the
   process is running. RoseMCP deliberately has no build in the loop ("there is no build in the loop",
   the server instructions). A hot-reload session inverts that at its *start*: you must have built,
   once, before launching. Is `rose_hot_reload_start` allowed to say "build first" -- or should it run
   one?
4. **What happens to the XAML live edit when C# hot reload exists?** They overlap: a XAML edit
   regenerates `.g.cs`, which is a C# delta. Two mechanisms that can both change a running app's
   appearance, with different failure modes and different persistence, is a tool-surface problem
   before it is an implementation one.
5. **Who owns the pairing?** Today a caller holds a `sessionId` and a workspace path and RoseMCP never
   relates them. Should `rose_debug_launch` learn to take a project from the workspace and launch its
   output -- which would make the pairing a consequence of launching rather than a thing to compute?
6. **UWP Debug: worth measuring early?** It is the flavour with the most unknowns and the most
   relevance to your day job. A half-day measuring whether UWP CoreCLR honours `CORDEBUG_JIT_ENABLE_ENC`
   might be worth doing inside M1 rather than at M11.

## Rose dogfooding notes

Read-only tools only, as instructed; no write tool, no reload, no close.

- **`rose_build_freshness`** on `RoseMcp.LiveApp` -- reached for to check the baseline precondition on
  a real project rather than reasoning about `BuildFreshness.cs`. **Worked, and produced a finding I
  would not have found by reading**: `newestSourcePath` came back as
  `obj\Debug\net10.0-windows\RoseMcp.LiveApp.GeneratedMSBuildEditorConfig.editorconfig`, a file MSBuild
  writes. That is HOT-10, and it exists because the tool answered with evidence instead of a summary.
  This is the tool working exactly as the pitch says.
- **`rose_symbol_info`** on `RoseMcp.LiveApp.Debugging.CorDebugSession.RememberModule` with
  `includeSource` -- reached for instead of a third `sed -n` into a 2,396-line file. **Worked well.**
  `declarationSpans` gave 1898-1910 including the doc comment, which is the range I wanted and not the
  range I would have guessed; the source came back with the XML summary attached. For a finding that
  turns on "what does this method keep and what does it drop", this beat reading the file.
- **`rose_search_symbols`** -- *not* reached for, and it should have been. I grepped for
  `CorDebugManagedCallback|SetManagedHandler` to find the callback wiring. The reason it lost is
  honest and worth recording: I was looking for a *usage pattern* across files ("where is the callback
  subscribed"), not a declaration, and `rose_find_references` needs a symbol name I did not yet have
  while `rose_search_symbols` finds declarations in *this* solution -- `CorDebugManagedCallback` is
  declared in ClrDebug, a referenced assembly. The gap: there is no "find uses of a metadata type"
  entry point that starts from a name I only half know. `rose_find_references` on a metadata symbol is
  the tool that would have won, and I did not believe it would work from a name alone.
- **Nothing hot-reload-shaped exists to reach for**, which is the finding this whole file is about.
  Worth noting for the agentic review: `rose_build_freshness`'s description already frames itself
  around "before running anything out of bin", which is one sentence away from being the hot-reload
  precondition's own prose.
- **External verification that `rose_*` could not help with, and nothing should**: reading assembly
  metadata out of the SDK's `dotnet-watch` folder and the NuGet cache. That was a PowerShell script
  over `System.Reflection.Metadata`. Worth saying only because it is the one class of question in this
  review that a Roslyn workspace genuinely has no answer for -- the worker sees the solution's
  references, not an arbitrary assembly on disk.
