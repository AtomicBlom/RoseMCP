# Worker and Roslyn layer

**Scope.** `src/RoseMcp.Worker` (118 files) and `src/RoseMcp.XamlStubs` (13 files), read in full for the
core named in the brief: `WorkspaceSession`, `DiskSynchronizer`, `SolutionWatcher`, `SolutionLoader`,
`WorkspaceHost`, `MemberEditService`, `MemberSyntax`, `BodyEdit`, `ChangeSignatureService` and its
`ParameterPlan`/`SignatureRewriter`/`CallSiteRewriter`/`CallSiteBinding`/`Forwarders`, `AddFileService`,
`DeclarationEditService`, `AttributeEdit`, `DocComment`, `ParamTags`, `MoveTypeService`, `MoveMemberService`,
`AddUsingService`, `FormatService`, `NameResolver`, `SymbolAddress`, `DeclarationLocator`, `SymbolLocator`,
`SymbolTarget`, `MetadataSymbols`, `EditVerification`, `Whitespace`, `LineEndings`, `UsingDirectives`,
`ResolvedImports`, `MissingImports`, `UnnecessaryUsings`, `CodeFixService`, `CodeFixCatalog`,
`DiagnosticsService`, `ShadowCopyAnalyzerAssemblyLoader`, `BuildProperties`, `RestoreRunner`,
`EvaluationInputs`, `GitDirectory`, `WorkspaceStatusReporter`, `NavigationService`, `OutlineService`,
`Tools/*.cs`, `ToolErrorReporting`, `Program`, `WorkerOptions`; the whole of `RoseMcp.XamlStubs`;
`RoseMcp.Solutions/BuildInfluencingFiles`. Skimmed: the request/result records, `Xaml/*`, `BuildFreshness`,
`TestProjects`, `ProjectItemStyle`. Tests inventoried by method name: `MemberEditTests` (57),
`ChangeSignatureTests` (25), `StalenessTests` (11), `MemberSyntaxTests` (22), `WhitespaceTests` (15),
`AnalyzerLockTests` (2). Also read: `CLAUDE.md`, the five invariant files named in the brief, and the
`no-roslyn-features-dependency`, `line-endings-in-code-a-caller-supplies`,
`a-failed-load-stays-failed-until-a-reload`, `automatic-imports-need-a-name-to-be-unique-everywhere`,
`a-sole-import-candidate-is-applied-whatever-namespace-it-is-in`, `add-file-does-not-edit-project-files`
and `a-named-argument-keeps-its-name` decisions. Issues #121, #195, #197, #199, #200, #210, #212, #217,
#218, #233, #246 read from GitHub. Rose itself was used against this worktree throughout; see the
dogfooding notes.

**Verdict.** Two different codebases share this project. The **freshness and loading core is strong**:
`WorkspaceSession` makes the read barrier the only way to obtain a `Solution`, `DiskSynchronizer`
commits its tracking table in the same step as the snapshot it describes, reload triggers are decided by
MSBuild's own import list rather than by event counts, and every one of those properties has an
integration test. The **navigation and analysis side is adequate to strong**: `EditVerification`'s
before/after delta over a version-keyed `DiagnosticsService` cache, `CallSiteBinding` asking `IOperation`
rather than counting arguments, and the boundary filter that forwards real messages are all the right
shapes. The **editing stack is fragile**, and for a structural reason rather than a local one: there is
a de facto pipeline (locate, rewrite, imports, format, whitespace, write, verify, resolve imports,
report) but it exists as a convention copied into six services, with five copies of the finish step,
four identical `IndentAt` helpers, and two different answers to "what is this file's line ending" inside
one service. Underneath that pipeline sits a *text* tier -- `BodyEdit` splicing strings by token span,
`ChangeSignatureService` discarding the declaration's own separators, `AddFileService` prepending imports
as text, `DocComment` sniffing the first character -- and all six open fidelity bugs (#195, #197, #199,
#200, #217, #218) live in that tier. Symbol addressing has one lossy primary resolver and a more
complete fallback that is reached only on one exception type, which is why a positional record property
is unreachable by name from every tool (reproduced live; broader than #233 describes). The Degraded
status on this repository is a Rose defect in the analyzer loader, not a fact about the repository.
Grade: **core strong, edges adequate, editing stack fragile** -- and the fragile part is the part the
project's own charter says it exists for.

## Strengths

Things a refactor must carry across intact.

- **The barrier is the only door.** `WorkspaceSession` keeps `_current` and `_workspace` private
  (`WorkspaceSession.cs:52-53`); the only public routes to a `Solution` are `ReadAsync` and
  `MutateAsync` (`:127-161`), and `WorkspaceHost` exposes nothing else (`WorkspaceHost.cs:103-106`).
  A new tool cannot bypass the freshness invariant; it can only forget conventions layered on top of
  it (see WRK-13, WRK-22). The single-consumer channel with `RunContinuationsAsynchronously` and the
  "caller's token abandons the wait, not the work" rule (`:435-438`) are exactly right.
- **Two-phase commit of the tracking table.** `DiskTrackerUpdate` is opaque by design
  (`DiskTrackerUpdate.cs:17-20`), the sweep computes and the session commits only on the path that
  adopts the snapshot (`WorkspaceSession.cs:268-279`), and a reload that throws leaves the table
  describing the snapshot still in hand. `StalenessTests.Absorbs_the_change_on_the_next_read_when_a_reload_throws`
  proves it. This is the hardest thing in the worker to get right and it is right.
- **Stat before read, defer mid-write.** `DiskSynchronizer.cs:228-235` and `:331-338`: the stamp is
  taken before the text so a write landing between the two is re-read next time, and a file that
  cannot be read is deferred rather than ingested truncated.
- **Reload decided by evaluation inputs.** `EvaluationInputs.Evaluate` (`EvaluationInputs.cs:60-101`)
  asks MSBuild for `Project.Imports` under the load's own global properties; the watcher remembers
  build files and nothing else (`SolutionWatcher.cs:157-175`). The two correctness gaps #246 names
  (an edited `<Import>`, a `Directory.Build.props` nearer the project) are already closed here and
  each has a test (`StalenessTests` lines 191, 230).
- **Refuse before the file is opened.** `MemberSyntax.ParseWrapped` parses inside a synthetic
  container and checks the shape, not only the syntax (`MemberSyntax.cs:357-388`);
  `DeclarationLocator` lists candidates on every refusal (`DeclarationLocator.cs:336-362`);
  `AttributeEdit.Several` refuses two attributes of one name rather than picking the first
  (`AttributeEdit.cs:289-297`); `GuardSharedDeclaration` refuses `int a, b;` (`MemberEditService.cs:1000-1011`).
- **Verification reports the delta, not the haystack.** `EditVerification.Delta` keys on
  id+file+message and counts duplicates (`EditVerification.cs:330-359`); `DiagnosticsService` caches
  per project against `GetDependentSemanticVersionAsync` and keeps the compiler and analyzer halves
  apart so a compiler-only request cannot poison the analyzer delta (`DiagnosticsService.cs:296-310`).
- **Arguments mapped by the compiler.** `CallSiteBinding.For` reads `IInvocationOperation.Arguments`
  and refuses any site it cannot bind (`CallSiteBinding.cs:73-110`); `CallSiteRewriter` moves the
  caller's own `ArgumentSyntax` nodes rather than regenerating them (`CallSiteRewriter.cs:81-96`);
  `ChangeSignatureService.Defects` names a mapping failure as the tool's own fault
  (`ChangeSignatureService.cs:663-678`). That last one is the model for WRK-07.
- **Error conversion is a registration, not a habit.** `WithToolErrorMessages` is a call-tool filter
  added once in `Program.cs:59`; no tool can forget it. The intent is right even though the filter
  itself is too permissive (WRK-07).
- **Result shapes are structural.** Every workspace result has `required long Revision` (checked
  across all 24 `*Result`/`*Report`/`*List` records in Contracts) and inherits `Workspace`/`WorkspaceKey`
  from `WorkspaceScopedResult`; writes inherit `ChangedFiles`/`Notices` from `WorkspaceMutationResult`.
- **The stub generator is an honest generator.** `XamlStubGenerator` is a real `IIncrementalGenerator`
  in its own assembly loaded as an `AnalyzerFileReference` (`SolutionLoader.cs:178-190`), the report
  travels through a generated document with only two constants crossing the boundary
  (`XamlStubReportChannel.cs:16-22`, guarded by `XamlStubChannelTests`), dialect selection carries its
  evidence (`XamlDialectSelector.cs:25-66`), and unresolved types are omitted and reported, never faked
  (`XamlStubEmitter.cs:52-71`).
- **Load diagnostics fold rather than truncate** (`LoadDiagnosticSummary.cs:46-82`), and
  `LoadedSuccessfully` is asked of the compilation rather than of MSBuild's chatter
  (`WorkspaceStatusReporter.cs:289`). On this repository that is exactly what keeps the WinUI
  design-time failures out of `degradedReasons` while still listing them.
- **The XML summaries say why.** Most of the worker's comments are in the "X, because Y" shape the
  conventions ask for, and the invariant documents are written from them. A refactor that loses these
  loses the reasoning.

## Findings

### WRK-01 The write pipeline is a convention copied into six services, not a type
- **Severity:** High
- **Effort:** M
- **Where:** `MemberEditService.cs:61-170`, `DeclarationEditService.cs:131-224`, `AddUsingService.cs:19-99`,
  `MoveMemberService.cs:31-112`, `AddFileService.cs:39-134`, `ChangeSignatureService.cs:34-141`
- **What:** Each service re-implements the same sequence: `RefuseIfMoved` (10 call sites),
  `new List<string>(snapshot.Notices)`, locate, rewrite, a private finish step that formats the annotated
  span and runs `Whitespace.Apply` and propagates to linked documents (five copies:
  `MemberEditService.FinishAsync:866`, `DeclarationEditService.FinishAsync:230`,
  `MoveMemberService.FormatAsync:361`, `ChangeSignatureService.NormalisedAsync:452`,
  `AddFileService.FormatAsync:366`), `SolutionWriter.ApplyAsync`, `EditVerification.RunAsync`, and a
  private `Notices()` iterator (six copies) that assembles the same result record with
  `IntroducedDiagnostics.Take(Listed)` where `Listed = 20` is declared in five files. The copies have
  already diverged: `ChangeSignatureService.NormalisedAsync` never calls `Formatter.FormatAsync`;
  `MoveMemberService.Notices` and `AddFileService.Notices` never yield `verification.Suggestions`;
  `DeclarationEditService.Notices` omits the "N error(s) were there before" line the others carry.
- **Why it matters:** Every fidelity rule (WRK-02, WRK-03) has to be fixed in five or six places, and
  the divergence above is the evidence that it is not. A seventh writing tool starts by copying one of
  the six and inherits whichever one it copied.
- **Suggested change:** Make the pipeline a type. `WriteOperation` (or `EditPipeline`) owns
  refuse-if-moved, locate, finish, write, verify, resolve-imports and report; a service supplies one
  stage, `Rewrite(DeclarationTarget, Placement, notices) -> AnnotatedRoot`, and the shape of its result.
  `MemberEditService`'s private `Written`/`Finished` records are already the right intermediate types;
  promote them. Pattern: template method over a value pipeline, with the report built by one
  `EditReport` type that every result record is projected from.

### WRK-02 Two tiers write source, and every open fidelity bug is in the text tier
- **Severity:** High
- **Effort:** M
- **Where:** `BodyEdit.cs:123` (string splice by token span), `BodyEdit.cs:203` (text splice),
  `BodyEdit.cs:406-426` (`Inserted` rebuilds a body from `statement.ToFullString().Trim()` joined with
  `\n`), `ChangeSignatureService.cs:342-346` (primary declaration takes the caller's separators and an
  `Unbroken` parenthesis), `AddFileService.cs:337` (imports prepended as text), `DocComment.cs:95-97`
  (`StartsWith('<')` decides XML vs prose), `Whitespace.cs:32` (falls back to the payload's own
  dominant ending)
- **What:** `rose_add_member` and `rose_replace_member` go syntax-in, syntax-out: parse in a container,
  `MemberSyntax.Prepared` sets trivia, `Formatter.FormatAsync` over the annotation, `Whitespace.Apply`
  over the span. Those two tools are the ones the invariants document and the ones that work. The paths
  above manipulate source as strings before the formatter sees it, and each open issue maps onto one:
  #195 (interior trivia dropped, leading trivia kept -- `Anchored` splices `body[..start] + replace +
  body[end..]` on token spans), #217 (`Inserted` discards every existing statement's leading trivia
  and hands the formatter a body to re-indent, which re-indents statements and not their wrapped
  arguments), #197 (`Separated(built, primary ? wanted : own)` throws the file's own layout away for
  the one declaration the caller named), #200 (`Build` never meets the file's own `using` block, while
  `UsingDirectives.Ensure` exists and is what `rose_add_using` uses), #199 (a comment arriving with
  `///` starts with `/`, so it is wrapped in `<summary>` and prefixed again), #218 (`RulesFor` with no
  analyzer config falls through to four spaces and `Dominant(text)` of the caller's own LF payload).
  `MemberSyntax` then carries five string re-indenters (`Shift`, `Reindented`, `Arranged`, `Placed`
  via `BodyEdit`, `Written`/`MixedIndentation` heuristics) whose job is to reconcile the caller's
  coordinate system with the file's before the formatter runs.
- **Why it matters:** This is the class of bug the brief asks about. It is not six bugs; it is one
  architectural line -- where text stops and syntax starts -- drawn in six different places. Each fix
  so far has added a heuristic (`Written`, `Copied`, `baseline`, `MixedIndentation`) rather than moving
  the line, and `#189` is acknowledged inside `MemberSyntax.cs:548` as a shape the heuristics cannot
  reach.
- **Suggested change:** Draw the line once: **syntax in, syntax out, text only inside
  `Whitespace.Apply`.** Concretely: `BodyEdit.Anchored` matches tokens (as now) but replaces the
  *statements* those tokens belong to with parsed statements via `ReplaceNodes`, so interior trivia is
  a decision (keep or replace) rather than a casualty; `BodyEdit.Inserted` becomes
  `block.WithStatements(block.Statements.Insert(index, prepared))` and touches no existing statement;
  `ChangeSignatureService.ChangeFor` uses `own` as the separator pattern for the primary too and gives
  a new parameter the trivia of its neighbour, exactly as `CallSiteRewriter.Continuation` already does
  on the call-site side (`CallSiteRewriter.cs:136-146`); `AddFileService.Build` parses the caller's
  unit and runs `UsingDirectives.Ensure` on it; `DocComment.Lines` parses with
  `SyntaxFactory.ParseLeadingTrivia` and branches on `DocumentationCommentTriviaSyntax`. Then replace
  the string re-indenters with **one trivia pass after the formatter**: for each token that begins a
  line inside the annotated span, set its leading whitespace to the destination indent plus the
  token's depth relative to the inserted node's first token. One implementation, applied to member,
  body, parameter list, attribute and doc comment alike.

### WRK-03 Five independent answers to "what is this file's indent and line ending"
- **Severity:** Medium
- **Effort:** S
- **Where:** `IndentAt` at `MemberEditService.cs:1212`, `DeclarationEditService.cs:294`,
  `ChangeSignatureService.cs:842`, `MoveMemberService.cs:392`; `BodyEdit.IndentOf:489`;
  `MoveTypeService.LineEnding:371-384`; `Whitespace.Dominant(text)` used as the ending at
  `MemberEditService.cs:189,344`, `DeclarationEditService.cs:154`, `MoveMemberService.cs:286,288,302`
  while `rules.LineEnding` (editorconfig first) is used at `MemberEditService.cs:465,902`;
  `MoveMemberService.cs:275` hardcodes `"\t"` as the indent unit
- **What:** Four byte-identical `IndentAt` methods; two line-ending policies inside one service
  (`ReplaceAsync` obeys the file's dominant ending, `AddAsync` obeys `.editorconfig`); a third policy
  in `MoveTypeService` that reads line 0 only; and a spaces repository receives a tab from
  `rose_move_member` because the indent unit is a literal rather than `rules.IndentUnit`.
  `rose_find_references` on `Whitespace.RulesFor` shows 15 call sites across 9 methods, each building
  its own `WhitespaceRules` for the same document.
- **Why it matters:** `end_of_line = crlf` in `.editorconfig` on a mixed-ending file is honoured by
  `rose_add_member` and ignored by `rose_replace_member`. Nothing reports the disagreement because both
  outcomes pass `dotnet format` on a consistent file and fail it on an inconsistent one.
- **Suggested change:** One `Placement` value (indent, indent unit, line ending, `WhitespaceRules`)
  computed once per edit by `Whitespace.PlacementAt(Document, position)`, passed down the pipeline
  from WRK-01. Delete the four `IndentAt`s and `MoveTypeService.LineEnding`.

### WRK-04 Two symbol resolvers, and the primary one is lossy
- **Severity:** High
- **Effort:** M
- **Where:** `DeclarationLocator.cs:118-125` (`SymbolFinder.FindSourceDeclarationsAsync` by last
  segment, then `address.Matches`), `DeclarationLocator.cs:229-286` (`NotFound` branches),
  `SymbolTarget.cs:58-76` (metadata fallback gated on `SymbolNotFoundException`),
  `MetadataSymbols.cs:68-87` (compilation-backed `GetTypesByMetadataName` + `GetMembers`)
- **What:** Reproduced live in this review: `rose_symbol_info` and `rose_find_references` on
  `RoseMcp.Contracts.SymbolLocation.TypeName` -- a positional record property -- both fail with
  "Nothing is declared at ... 'TypeName' is declared as RoseMcp.Contracts.LiveEvaluation.TypeName, ...
  and 7 more". The declaration index behind `FindSourceDeclarationsAsync` does not list a property
  synthesised from a record parameter, so `matching` is empty; because *other* types declare a
  `TypeName`, `named` is not empty, so the `matching.Count == 0` branch throws a plain
  `ArgumentException` (`:249-271`) rather than `SymbolNotFoundException` (`:238-242`), and the
  fallback in `SymbolTarget.ResolveAsync` never runs. #233 reported the read side working only because
  `ModuleSimpleName` happened to be unique in the solution. The fallback resolver, `MetadataSymbols`,
  asks the compilation (`GetTypesByMetadataName` then `GetMembers(name)`) and would find it -- it is
  the more complete of the two and is used last. `MoveTypeService.Select:104-141` is a third
  resolver that matches syntax identifiers in one file.
- **Why it matters:** Positional records are the shape of every DTO in `RoseMcp.Contracts`, and their
  properties cannot be addressed by name from any tool whenever the name is common -- which for
  `Name`, `Path`, `Line`, `TypeName` is always. The refusal points at `rose_search_symbols`, which
  hands back the same address. The rename tool's description tells callers to prefer names over
  positions.
- **Suggested change:** One resolver, compilation-backed: parse the address; for each project,
  resolve the type path (`GetTypesByMetadataName` with the arity loop `MetadataSymbols.TypesNamed`
  already has, *type-first* so `Namespace.Type.Type` is tried as a type before a constructor);
  `GetMembers(last)` filtered by `ParametersMatch`; prefer symbols with `Locations.Any(IsInSource)`;
  derive declarations from `DeclaringSyntaxReferences`. Keep `FindSourceDeclarationsAsync` only for a
  bare single-segment name. `SymbolTarget`, `DeclarationLocator` and `MoveTypeService.Select` all call
  it. Add positional-record property, primary-constructor parameter, and `Ns.Type.Type` cases to
  `SymbolAddressTests` and an integration test over `tests/fixtures`.

### WRK-05 A repeated last segment is always read as a constructor
- **Severity:** Medium
- **Effort:** S
- **Where:** `SymbolAddress.cs:259-268`
- **What:** `RoseMcp.XamlDiff.XamlDiff` parses as constructor of `XamlDiff` in namespace `RoseMcp`;
  reproduced live: `rose_symbol_info` answers "'XamlDiff' declares no constructor ... Add one with
  rose_add_member" (#210). The comment justifying it ("a member may not share the name of the type
  enclosing it") is true of members and false of namespaces.
- **Why it matters:** Every `Foo.Bar/Bar.cs` layout is unreachable by qualified name, and the error
  recommends the call that just failed.
- **Suggested change:** `SplitOffConstructor` returns both readings when the last two segments repeat;
  the resolver (WRK-04) tries the type reading first. Fold into WRK-04 if that lands first.

### WRK-06 `NameResolver` asks one compilation about another compilation's symbol
- **Severity:** High
- **Effort:** S
- **Where:** `NameResolver.cs:197`
- **What:** `GatherAsync` calls `SymbolFinder.FindDeclarationsAsync(project, ...)`, which returns
  symbols owned by referenced projects' compilations, then asks
  `compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly)` on the asking project's
  compilation. Roslyn throws `ArgumentException("symbol must be from this compilation or some
  referenced assembly")`, which the boundary forwards verbatim (#121, #212). The issue's table shows
  it depends on the asking project, which is what a cross-compilation symbol looks like.
- **Why it matters:** `rose_resolve_name`, `rose_add_file` and every write tool with `resolveUsings`
  fail with a message about an argument the caller never passed. #212 cost a retry on a staleness
  theory before the cause was guessed.
- **Suggested change:** Map the candidate into the asking compilation before the check --
  `SymbolKey.Create(symbol).Resolve(compilation).Symbol`, or `compilation.GetTypeByMetadataName` for
  types -- and skip a candidate that does not map rather than throwing. Regression test with two
  fixture projects, one referencing the other, resolving a name declared in the referenced one.

### WRK-07 The boundary cannot tell a deliberate refusal from a leaked framework exception
- **Severity:** Medium
- **Effort:** S
- **Where:** `ToolErrorReporting.cs:52-55` (`Explainable`), refusals thrown as plain
  `ArgumentException`/`InvalidOperationException` throughout (`DeclarationLocator.cs:239`,
  `MemberEditService.cs:195`, `SymbolLocator.cs:96` `ArgumentOutOfRangeException`)
- **What:** The filter forwards any non-cancellation exception with a message. Rose's own refusals
  and Roslyn's or the BCL's `ArgumentException`s are the same CLR type, so `(Parameter 'symbol')`
  (WRK-06) and `(Parameter 'line')` reach the caller reading exactly like a considered refusal.
  `ChangeSignatureService.Defects` (`:663-678`) shows the project knows the distinction matters, but
  only for one class of failure.
- **Why it matters:** The result-shapes invariant says an error says what went wrong. A leaked
  framework message says something went wrong *elsewhere*, in vocabulary the caller cannot act on,
  and it is indistinguishable from advice.
- **Suggested change:** A `Refusal : Exception` base (or `ToolRefusal`) for every deliberate throw;
  the filter forwards a `Refusal` verbatim and wraps anything else as "rose_x hit an internal error
  (report it): {message}". Enforce with a banned-API or Roslyn analyzer in `src/RoseMcp.Worker`
  forbidding `throw new ArgumentException`/`InvalidOperationException` outside the `Refusal`
  hierarchy. See inversion 4.

### WRK-08 The shadow-copy loader flattens every analyzer into one `AssemblyLoadContext`, which is why this repository is Degraded
- **Severity:** High
- **Effort:** M
- **Where:** `ShadowCopyAnalyzerAssemblyLoader.cs:45-50` (dependency resolver keyed by simple name),
  `:101-116` (`LoadFromPath` catches `FileLoadException` and retries by *name*), `:153`
  (`_shadowBySimpleName` last writer wins), `SolutionLoader.cs:282-315`
- **What:** Diagnosed from disk, not from the code alone. Two copies of
  `Microsoft.Extensions.Logging.Generators.dll` reach this solution: the ASP.NET Core targeting pack
  `Microsoft.AspNetCore.App.Ref/10.0.10/analyzers/dotnet/cs/` ships assembly version **10.0.14.32716**
  (taken by `RoseMcp.Broker`, `RoseMcp.Server`, `RoseMcp.Tray` through the framework reference), and
  NuGet `Microsoft.Extensions.Logging.Abstractions/10.0.11/analyzers/dotnet/roslyn4.4/cs/` ships
  **10.0.14.37416** (taken by `RoseMcp.Worker`, `RoseMcp.Logging`). Both are loaded into
  `AssemblyLoadContext.Default`. The second `LoadFromAssemblyPath` throws `FileLoadException` because
  an assembly of that simple name is already loaded; the fallback `LoadFromAssemblyName(name)` asks
  for `.37416` while `.32716` is resident, and the runtime answers "The located assembly's manifest
  definition does not match the assembly reference" -- the exact text in `analyzerLoadFailures`. The
  same thing happens to `Microsoft.Extensions.Options.SourceGeneration.dll`. The brief's reading
  ("10.0.14 located vs 10.0.11 referenced") is not what is happening: the package version is 10.0.11,
  but both assembly versions are 10.0.14.x, differing in build number. `csc` handles this because each
  compilation loads its own analyzers; Roslyn's own `AnalyzerAssemblyLoader` handles it with one
  `AssemblyLoadContext` per analyzer directory. **This is a Rose defect, not a fact about the repo.**
  The other half of the Degraded report -- "Cannot resolve Assembly or Windows Metadata file ...
  RoseMcp.Contracts.dll" on the WinUI projects -- *is* a fact about the checkout: `src/RoseMcp.Contracts/bin`
  does not exist in this worktree, and `UseWinUI` runs the XAML compiler during the design-time build,
  which needs referenced assemblies on disk. Rose classifies that correctly (a load diagnostic, not a
  degraded reason, since `LoadedSuccessfully` is true), though the remedy ("build the referenced
  project first") is not said.
- **Why it matters:** Any solution mixing a framework reference and a NuGet reference to the same
  Microsoft.Extensions package -- most ASP.NET solutions with a class library -- loses one copy's
  generators, `LoggerMessage` stubs go missing, and the status blames the repository ("Rebuild them,
  or check their dependencies and the Roslyn version they were built against").
- **Suggested change:** One `AssemblyLoadContext` per shadow directory (mirror Roslyn's
  `DirectoryLoadContext`), resolving dependencies from that directory first and falling back to
  Default only for Roslyn's own assemblies. Keep the shadow copy. Add a test to `AnalyzerLockTests`
  that loads two versions of one analyzer assembly and asserts both produce generators. Say
  "build the referenced project" in the WinUI load-diagnostic summary.

### WRK-09 `DiagnosticsService` never evicts, and a reload orphans every entry
- **Severity:** Medium
- **Effort:** S
- **Where:** `DiagnosticsService.cs:27,106,133`; `WorkspaceSession.ReloadAsync:379-396`
- **What:** The cache is keyed by `ProjectId`. `ReloadAsync` opens a new `MSBuildWorkspace`, so every
  project gets a new id and the old entries are never touched again. Each entry holds
  `ImmutableArray<Diagnostic>`, and a `Diagnostic`'s `Location.SourceTree` retains the tree and its
  text -- so each reload pins the previous solution's syntax trees for the life of the worker.
- **Why it matters:** The worker is meant to live for hours; a day of branch switches accumulates a
  solution's worth of trees per reload.
- **Suggested change:** `DiagnosticsService.Forget(IEnumerable<ProjectId>)` called from `ReloadAsync`
  with the ids that vanished, or key the cache on `ProjectId` and clear it wholesale on reload (the
  new ids miss anyway).

### WRK-10 Mutations compile on the single writer, so every read queues behind a verification
- **Severity:** Medium
- **Effort:** M
- **Where:** `WorkspaceSession.MutateAsync:135-162` (the whole `mutation` runs inside `EnqueueAsync`),
  `ChangeSignatureService.cs:105-111` (`EditVerification.AllProjects`)
- **What:** The design note says "the expensive part of a read ... happens off the writer"
  (`WorkspaceSession.cs:15-17`), and that is true of reads. A mutation, though, runs locate, rewrite,
  format, write *and* the before/after compile on the pump. `rose_change_signature` with `verify`
  compiles the whole solution twice while holding the writer; every `rose_symbol_info` from any client
  waits.
- **Why it matters:** In http mode with several clients, one signature change on a 50-project solution
  stalls all navigation for the duration of two solution compiles.
- **Suggested change:** Split a mutation into `Prepare(snapshot) -> (Solution after, T result)`
  computed off-pump against the barrier snapshot, and a short on-pump `Commit(after, expectedRevision)`
  that re-checks the revision (turning `RefuseIfMoved` into the commit's compare-and-swap) and adopts
  the solution. Verification runs off-pump on `after`; the write to disk happens inside `Commit` so
  the self-write bookkeeping stays where it is. Pattern: optimistic concurrency with a serialised
  commit.

### WRK-11 `_selfWritten` is not drained when a mutation throws
- **Severity:** Low
- **Effort:** S
- **Where:** `WorkspaceSession.cs:141-143`, `:164-179`
- **What:** `TakeSelfWrites()` runs after `await mutation(...)`. The summary says "Drained whether or
  not the snapshot was adopted, so a dry run or a failure cannot leave paths behind", but an exception
  skips the drain; `SolutionWriter` notes each path *before* writing it (`SolutionWriter.cs:48,78`), so
  a write that fails on file three leaves three paths for the next mutation to restamp.
- **Why it matters:** Narrow: the next barrier's sweep re-reads the files before the restamp, so the
  window is between the sweep and `AcceptSelfWrites` on the following mutation. It is a comment that
  is untrue about a correctness mechanism, which is worse than the bug.
- **Suggested change:** `try { ... } finally { TakeSelfWrites(); }`, and restamp only on adoption as now.

### WRK-12 Any build file appearing anywhere reloads, and a touch with identical bytes reloads
- **Severity:** Medium
- **Effort:** S
- **Where:** `DiskSynchronizer.cs:203` (`created.Any(BuildInfluencingFiles.IsBuildFile)`),
  `DiskSynchronizer.DetectStructuralChange:550-564` (stamp = `(LastWriteUtc, Length)`)
- **What:** The watcher's appearance list is filtered by *kind* only, so a `.csproj` or `.props` created
  under `tests/fixtures/` -- which this repository's own tests do -- reloads `RoseMcp.slnx`. Separately,
  structural files are compared by stamp, so `git checkout` rewriting `Directory.Build.props` with the
  same content triggers a full design-time build. #246 asks for exactly the opposite. Note that
  #246's description of the triggers (50 events in 500 ms, 200 files appearing, `.git/HEAD`) no longer
  matches this branch -- those were removed -- so the issue text is stale; the two cases here are what
  remains of it.
- **Why it matters:** A reload is a full design-time build of every project (20 s on this repository,
  minutes on a UWP app), paid for a file no project imports.
- **Suggested change:** An appearing build file reloads only when it is in `_absentBuildFiles`, is a
  project the solution lists, or is importable while some project is unevaluated. Hash structural
  files (they are small) so a touch without a change is absorbed; keep the stamp as the fast path.

### WRK-13 `WorkspaceSnapshot` doubles as a tuple, with `Revision = 0` fabricated three times
- **Severity:** Medium
- **Effort:** S
- **Where:** `EditVerification.cs:77,299`, `AddFileService.cs:406`, `MemberEditService.cs:849`
- **What:** `NameResolver.ResolveAsync`, `MissingImports.SuggestAsync` and `DiagnosticsService.AnalyseAsync`
  take a `WorkspaceSnapshot` but need only a `Solution`, so callers with a derived solution build a
  fake snapshot. The barrier-produced value and a synthesised one are the same public type with a
  public `init` constructor.
- **Why it matters:** The freshness invariant is "reads never observe a snapshot older than disk".
  The type that carries that guarantee can be minted by anyone, so the guarantee is by convention the
  moment a `Solution` leaves the tool boundary. Today every fake is derived from a real snapshot, so it
  is a smell rather than a bug -- but it is the smell that becomes the bug.
- **Suggested change:** Give the three services `Solution`-taking overloads (notices are the caller's
  to merge); make `WorkspaceSnapshot`'s construction `internal` to the session (a factory method on
  `WorkspaceSession`), so the only producer is the barrier. See inversion 1.

### WRK-14 Five project-by-name resolvers with two different refusal policies
- **Severity:** Medium
- **Effort:** S
- **Where:** `GeneratedDocumentService.Select:125-137` (widens to the whole solution with a notice),
  `DiagnosticsService.SelectProjects:254-287` (refuses, listing names), `NavigationService.GuardProject:148-158`
  (refuses), `AddFileService.Owner:169-178` (refuses), `ProjectGraphService.Describe:29-34` (refuses),
  `BuildFreshness.Of:29-34` (returns empty; `AnalysisTools.cs:63` adds a notice)
- **What:** `DiagnosticsService`'s own comment explains why widening is the worst answer ("comes
  back clean and complete, for a question fourteen projects larger than the one asked").
  `GeneratedDocumentService` does the thing that comment forbids. Some accept a path, some do not;
  all are case-insensitive by their own arrangement.
- **Why it matters:** `rose_read_generated_document project=Typo` searches every project and reports
  documents from all of them as if they were the one asked about.
- **Suggested change:** `Projects.Named(Solution, string)` in one place, refusing with the sorted
  list; every service calls it.

### WRK-15 `BodyEdit.Inserted` rebuilds the whole body from trimmed statements
- **Severity:** Medium
- **Effort:** S
- **Where:** `BodyEdit.cs:406-426`
- **What:** `position: start|end` joins `statement.ToFullString().Trim()` for every existing
  statement with `\n`, then relies on the formatter to re-indent. Blank lines between statements are
  lost and wrapped continuations inside existing statements are re-based -- #217's "nine registrations
  all rewritten". Listed separately from WRK-02 because it is a one-afternoon fix with a test already
  in place (`MemberEditTests.Inserts_before_a_closing_return`).
- **Why it matters:** The damage scales with the member being appended to and is invisible in the
  tool's success report.
- **Suggested change:** Insert parsed statements into `block.Statements` and leave the others' trivia
  untouched; only the new statements get `Prepared` trivia and the annotation.

### WRK-16 `SymbolLocator.FindDocument` scans every document with `Path.GetFullPath`, once per diagnostic
- **Severity:** Low
- **Effort:** S
- **Where:** `SymbolLocator.cs:41-49`; called from `MissingImports.NameAtAsync:142` per unresolved
  diagnostic, and from `FormatService`, `MoveTypeService`, `RequireDocument`
- **What:** `solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(... GetFullPath ...)` is
  O(documents) with a filesystem-normalising call per document. `Solution.GetDocumentIdsWithFilePath`
  is indexed and is already used at `AddFileService.cs:142`.
- **Why it matters:** On a 50-project solution an edit introducing five unresolved names walks
  thousands of documents five times before answering.
- **Suggested change:** Use `GetDocumentIdsWithFilePath(Path.GetFullPath(filePath))`; keep the
  three-way refusal in `NoDocument`.

### WRK-17 History and issue-number comments the repository's own convention forbids
- **Severity:** Low
- **Effort:** S
- **Where:** `DiagnosticTarget.cs:8,36` ("It used to be one argument", "used to analyse"),
  `ChangeSignatureService.cs:499-504` ("used to report ... #59 hit both halves"),
  `NavigationService.cs:246` ("were previously answered"), `DiskSynchronizer.cs:186` ("what this used
  to need"), `WorkspaceHost.cs:65-71` ("These were being appended"), `MemberEditService.cs:1110`
  ("it used to be the point")
- **What:** `CLAUDE.md` says a comment never says what the code was before, and a closed issue's
  number is a tag to drop. These are the vibe-coded residue the brief asks about: each is a commit
  message written into a summary.
- **Why it matters:** Small individually; collectively they teach the next session that the
  convention is optional.
- **Suggested change:** Rewrite each as a timeless consequence. Add a unit test that scans
  `src/**/*.cs` comment lines for `used to|previously|no longer|were being|for now` -- the repository
  already uses self-checking tests of this kind (`XamlStubChannelTests`, `TestProjects.Recognises`).

### WRK-18 Duplicated constants and helpers the codebase elsewhere knows how to centralise
- **Severity:** Low
- **Effort:** S
- **Where:** `IgnoredDirectories` at `DiskSynchronizer.cs:35` and `SolutionWatcher.cs:28` ("The same
  list the watcher ignores" -- by copy); `SeparatorChars` in both; `SamePath` at `EditVerification.cs:380`,
  `ResolvedImports.cs:177`, `DeclarationLocator.cs:375`, `DiagnosticsService.PathMatches:289`,
  `BuildFreshness.cs:147` (this one with the try/catch the others lack); `SafeGetGenerators` at
  `WorkspaceStatusReporter.cs:253` (records the failure) and `GeneratedDocumentService.cs:139`
  (swallows it); unresolved-id lists at `MemberEditService.cs:52`, `MissingImports.cs:30`,
  `ResolvedImports.cs:268` with deliberate one-id differences documented in each; dead parameters
  silenced with `_ = declaration;` (`BodyEdit.cs:397`), `_ = wanted;` (`BodyEdit.cs:549`),
  `_ = project;` (`AddFileService.cs:335`)
- **What:** `BuildInfluencingFiles` shows the project knows the fix ("Three lists of these had drifted
  apart"); these are the next three lists.
- **Why it matters:** `SafeGetGenerators` is the one that bites: a generator that throws on load is
  recorded as a degraded reason by status and silently ignored by `rose_list_generated_documents`,
  which then says "loaded 1 generator(s) but none produced output".
- **Suggested change:** `Paths.Same`, `SourceDirectories.Ignored` in `RoseMcp.Solutions`; one
  `AnalyzerReferences.Generators(reference, language, onFailure)`; one `UnresolvedNames` type with the
  three sets named for what they mean; delete the dead parameters.

### WRK-19 `Whitespace.RulesFor` falls back to Roslyn defaults and to the payload's own ending, then `rose_format` certifies the result
- **Severity:** Medium
- **Effort:** S
- **Where:** `Whitespace.cs:26-37` (`Dominant(text)` fallback), `:348-361` (four spaces when nothing
  says), `FormatService.cs:104-114`
- **What:** With no `AnalyzerConfigOptions` for the document -- a project whose design-time build did
  not attach `.editorconfig`, or a document in a project new to the workspace -- the indent is four
  spaces and the ending is whatever the *payload* mostly uses, which for a new file is the caller's
  LF. `rose_format` then reports "Every file was already formatted" against the same empty rules
  (#218).
- **Why it matters:** The writing tool and the checking tool agree with each other and disagree with
  `dotnet format`, and the caller has done everything the documentation asked.
- **Suggested change:** Fall back in order: analyzer config; a sibling `.cs` document in the same
  project (`Dominant` of *its* text, indent read from *its* first indented line); the nearest
  `.editorconfig` on disk parsed with `Microsoft.CodeAnalysis.AnalyzerConfig.Parse` (public). Never
  read the ending of a new file from its own payload. When every source is silent, say so in a notice
  instead of "already formatted".

### WRK-20 Status and generated-code lookups run every project's generators
- **Severity:** Medium
- **Effort:** M
- **Where:** `WorkspaceStatusReporter.DescribeProjectsAsync:85-87` (every project, every status call),
  `SymbolLocator.GeneratedHintNameAsync:240-257` (loops *all* projects' generated documents for each
  location whose path is not on disk), called from `SymbolLocator.DescribeAsync` per reference
- **What:** `rose_find_references` on a symbol used in generated code (TUnit's registration, a
  `LoggerMessage` stub) runs `GetSourceGeneratedDocumentsAsync` on every project once per hit. Roslyn
  caches per compilation version so the cost is paid after each edit, but on a XAML solution "each
  edit" regenerates every stub.
- **Why it matters:** Status is the call a caller makes when something looks wrong; making it the
  slowest call teaches them not to.
- **Suggested change:** Resolve the owning project from the location's tree (`solution.GetProject`
  on the compilation that produced the symbol) and ask only it; memoise hint names per call. For
  status, keep the generator run behind an `includeGenerated` flag or reuse the last completed
  description when the dependent semantic version has not moved.

### WRK-21 `rose_search_symbols` returns source-generated declarations beside real ones
- **Severity:** Low
- **Effort:** S
- **Where:** `NavigationService.SearchAsync:332-380`
- **What:** Searching `SymbolLocation` returned four matches: the record, its test class, and two
  TUnit-generated registration members from `obj/.../TUnit.Core.SourceGenerator/`. They carry
  `generatedHintName`, so the information is there; the ordering by name length puts them among
  the results rather than after.
- **Why it matters:** Half the answer to a common query is noise the caller cannot act on.
- **Suggested change:** Sort generated declarations last, or exclude them unless asked; the same
  `IsGenerated` test `OutlineService.DescribeMemberAsync:189-190` already uses.

### WRK-22 The `no-roslyn-features-dependency` decision argues against the wrong thing
- **Severity:** Low
- **Effort:** S
- **Where:** `docs/decisions/no-roslyn-features-dependency.md`; `UsingDirectives.cs` (313 lines),
  `MoveMemberService.Access:387-388`, `MoveMemberService.ImportAsync:205-224`
- **What:** The decision's reason 3 (referencing Features would offer the IDE's add-import fix beside
  `rose_resolve_name`) is not so: `CodeFixCatalog` discovers fixers by reflection over the *project's*
  analyzer references (`CodeFixCatalog.cs:59-64`), not the worker's own; nothing would register. Reason
  2 (version coupling) is real but already paid -- `RoseMcp.XamlStubs` pins the worker to Roslyn 5.9.0
  exactly. What the decision does not say is the decisive fact: Features' refactoring services
  (`IChangeSignatureService`, `MoveTypeService`, `AddImport`) are `internal`, reachable only via
  `GetLanguageService<T>()` with internal interfaces, so the reference would buy little legitimately.
  Meanwhile the cost actually being paid is unused *public* Workspaces API: `ImportAdder.AddImportsAsync`,
  `SyntaxGenerator`, `DocumentEditor`, `Simplifier.ReduceAsync` (all present in the pinned 5.9.0
  `Microsoft.CodeAnalysis.Workspaces.xml`, zero uses in the worker). `UsingDirectives` reimplements
  `ImportAdder`'s placement; `MoveMemberService.Access` builds `Type.Member` by string where
  `Simplifier` would reduce a fully qualified name to the file's own spelling.
- **Why it matters:** A decision with the wrong reasons gets re-litigated, and this one is about to
  be: Edit-and-Continue analysis lives in Features (see the hot-reload section).
- **Suggested change:** Amend the decision: (a) Features' services are internal, so the reference is
  not the choice being made; (b) list the public Workspaces APIs and decide each (adopt `ImportAdder`
  behind `UsingDirectives`'s scope check, `Simplifier` for qualification); (c) record that hot reload
  is what changes the answer, and name `Microsoft.CodeAnalysis.ExternalAccess.HotReload` as the route
  to evaluate.

### WRK-23 Tool-layer boilerplate is repeated where two helpers already exist
- **Severity:** Low
- **Effort:** S
- **Where:** `Tools/RefactoringTools.cs:37-56, 84-103, 122-138, 158-175, 319-339, 359-377, 508-530`
  (inline `Split`/`Follow`/`SessionAsync`/`MutateAsync`), against `RunAsync:614-626` and `EditAsync:384-398`
- **What:** The tool layer is thin, which is right: each tool builds a request record and hands it to
  a service through `MutateAsync` or `ReadAsync`. But seven mutation tools inline the same five lines
  that `RunAsync` wraps, and `EditAsync` is `RunAsync` specialised for one lambda shape. A new tool
  copies whichever it sees first.
- **Why it matters:** Low on its own; it is the tool-layer half of WRK-01 and disappears with it.
- **Suggested change:** Every mutation tool goes through `RunAsync`; add `ReadAsync` for the reads in
  `NavigationTools`/`AnalysisTools` so the `Follow` handle cannot be forgotten either.

## Pit-of-success inversions

1. **Rule today:** a read never observes a snapshot older than disk, and derived work must not fake a
   snapshot. **Mechanism:** make `WorkspaceSnapshot` mintable only by `WorkspaceSession` (internal
   factory); give `NameResolver`/`MissingImports`/`DiagnosticsService` `Solution` overloads. Then a
   `Revision = 0` fake will not compile (WRK-13).
2. **Rule today:** every mutation calls `snapshot.RefuseIfMoved(request.ExpectedRevision)` first (ten
   services). **Mechanism:** `MutateAsync(long? expectedRevision, Func<...> prepare)` performs the check
   at commit, as a compare-and-swap on the revision; services lose the call and cannot forget it. Falls
   out of WRK-10.
3. **Rule today:** whatever writes C# ends formatted, indented for where it goes, with the file's
   endings, and reports what a diff cannot show. **Mechanism:** the pipeline type of WRK-01 with the
   `Placement` value of WRK-03; a service implements only `Rewrite`, so it has no access to text,
   `IndentAt`, or `Whitespace.Dominant`. Delete the four `IndentAt`s so there is nothing to copy.
4. **Rule today:** an error says what went wrong; refusals carry advice. **Mechanism:** a `Refusal`
   exception hierarchy plus a banned-API analyzer (`BannedSymbols.txt` in the worker project) that
   forbids `new ArgumentException(...)`/`new InvalidOperationException(...)` outside it; the boundary
   filter wraps anything that is not a `Refusal` as an internal error (WRK-07).
5. **Rule today:** a name resolves one way. **Mechanism:** one `SymbolResolver.ResolveAsync(Solution,
   SymbolAddress, ResolveOptions)` that both `SymbolTarget` and `DeclarationLocator` call; `MetadataSymbols`
   becomes its metadata branch rather than a fallback on one exception type (WRK-04, WRK-05).
6. **Rule today:** analyzers are shadow-copied (tested) and two versions of one generator must both
   load (untested, broken). **Mechanism:** one `AssemblyLoadContext` per shadow directory, and a test
   in `AnalyzerLockTests` that loads two versions of one analyzer and asserts two generator sets
   (WRK-08).
7. **Rule today:** a project name that matches nothing is refused, never widened. **Mechanism:** one
   `Projects.Named` that every service must go through to turn a string into `Project`s (WRK-14).
8. **Rule today:** comments carry no history. **Mechanism:** a unit test over `src/**/*.cs` comment
   lines for the forbidden phrases, in the same style as the repository's existing drift tests (WRK-17).

## Hot-reload relevant facts

For the reviewer writing `07-hot-reload-readiness.md`. Everything here is read from the worker as it is
on this branch.

- **There is no Edit-and-Continue code anywhere.** `grep` for `EmitDifference`, `EmitBaseline`,
  `SemanticEdit`, `EditAndContinue` and `.Emit(` across `RoseMcp.Worker`, `RoseMcp.LiveApp` and
  `RoseMcp.Broker` returns nothing. The worker never emits; it compiles for diagnostics only.
- **The worker does not hold `Compilation` objects; it holds one `Solution`.** `WorkspaceSession._current`
  (`WorkspaceSession.cs:53`) is the only retained solution. Compilations are obtained on demand
  (`project.GetCompilationAsync` at `DiagnosticsService.cs:118`, `NameResolver.cs:188`,
  `MetadataSymbols.cs:43`, `CodeFixService.cs:224,266`) and cached by Roslyn inside the solution's
  project states, so the *current* compilations stay warm for as long as `_current` is held. Previous
  solutions are dropped on every adoption (`_current = ...` at `:146`, `:284`, `:389`). Because a
  `Solution` is an immutable value sharing trees with its successors, retaining a baseline solution is
  cheap -- nothing does it today.
- **"Solution as of last emit" versus "solution now" is not representable, but the hook exists.** Both
  ways the solution changes -- our own writes (`MutateAsync`, `:135-162`) and disk changes absorbed by
  the barrier (`ReconcileAsync`, `:282-285`) -- advance one counter, `_revision`. A hot-reload service
  would pin a baseline `(Solution, Revision, EmitBaseline)` and compare against the snapshot the barrier
  hands it; the revision is the identity. Note the counter is per solution, not per project.
- **The per-project change key already in use is Roslyn's own.** `Project.GetDependentSemanticVersionAsync`
  keys the diagnostics cache (`DiagnosticsService.cs:104`). It is also the right test for "does this
  project have edits since the baseline".
- **Output paths and freshness are known.** The design-time build supplies `Project.OutputFilePath`
  (`BuildFreshness.cs:40`, `ProjectGraphService.cs:59`), which is where `ModuleMetadata.CreateFromFile`
  would read the baseline module for `EmitBaseline.CreateInitialBaseline`. `BuildFreshness.Of` already
  answers "is the on-disk assembly the one this source compiled to" (`Stale == false` per project),
  which is exactly the precondition for a valid initial baseline. `RoseMcp.Symbols` already reads
  portable PDBs (method tokens, local slots), which is the input side of the
  `EditAndContinueMethodDebugInformation` reader `CreateInitialBaseline` needs.
- **The emit half is public and present in the pinned Roslyn.** In `Microsoft.CodeAnalysis 5.9.0`'s XML
  docs: `Compilation.EmitDifference` (4 overloads), `EmitBaseline.CreateInitialBaseline`, `SemanticEdit`.
  The **analysis half is not**: computing `SemanticEdit`s from a syntax diff and classifying rude edits
  (`AbstractEditAndContinueAnalyzer`, `EditSession`) is `internal` to `Microsoft.CodeAnalysis.Features`,
  which the `no-roslyn-features-dependency` decision excludes (WRK-22). The local SDK ships dotnet-watch's
  route over it: `sdk/10.0.302/DotnetTools/dotnet-watch/.../tools/net10.0/any/` contains
  `Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll`, `Microsoft.CodeAnalysis.Features.dll` and
  `Microsoft.DotNet.HotReload.Watch.dll`. That is the supported-ish entry point to evaluate, and it comes
  with Features.
- **Text ingestion is whole-file.** `DiskSynchronizer.TryReadAsync` builds `SourceText.From(stream)`
  with no `TextChangeRange` (`DiskSynchronizer.cs:580-596`), so an external edit is a full reparse of
  that document. EnC's syntax diff works on trees and is indifferent; it only costs parse time.
- **Generated code is regenerated per compilation.** `XamlStubGenerator` combines with
  `CompilationProvider` (`XamlStubGenerator.cs:38`), so every edit re-runs the stub emit; output is
  deterministic (`XamlStubReportChannel.NewLine` is fixed), so generated trees should diff to nothing
  when the markup did not change. EnC treats generated documents as source.
- **`LoadMetadataForReferencedProjects = true`** (`SolutionLoader.cs:86`) turns a referenced project
  that is not in the solution into a metadata reference to its on-disk output; for EnC that output has
  to be the assembly the process is running.
- **Process topology.** The worker has no debugger and LiveApp has no Roslyn (`RoseMcp.LiveApp` owns
  ICorDebug; `ICorDebugModule2::ApplyChanges` is its call to make). Deltas from `EmitDifference` --
  metadata, IL, PDB byte arrays -- would cross worker -> broker -> LiveApp. The worker's single writer
  means an "apply" is mutation-shaped (ordered) while the emit is a read (off-pump); that split is
  natural if WRK-10 lands.
- **Threading.** All Roslyn work in the worker is `async` over the thread pool with no
  synchronisation context; nothing pins a thread, so a long `EmitDifference` would sit alongside a
  compile with no special handling.

## Open questions for Steve

1. `writing-csharp.md` states that `Formatter.FormatAsync` leaves a wrapped parameter list where it
   arrived. Was that measured with the formatter run over the *annotated span* only, or over the whole
   document? Roslyn's anchor-indentation operations preserve a continuation's offset *relative to its
   statement's first token*, so the two runs can behave differently, and the answer decides whether the
   string re-indenters in `MemberSyntax` can be replaced by a trivia pass (WRK-02).
2. Is hot reload meant to emit in the worker (retaining a baseline `Solution`) or somewhere else?
   Which process owns the "committed solution"?
3. #246's description of reload triggers does not match this branch (no event-count or `HEAD`
   triggers remain; imports are tracked). Is the issue stale, or does another branch still carry
   the older `SolutionWatcher`?
4. Should the WinUI design-time failure ("Cannot resolve ... RoseMcp.Contracts.dll") carry a remedy in
   status ("build RoseMcp.Contracts first")? Rose classifies it correctly; it just does not say what to do.
5. `rose_search_symbols` returns source-generated declarations. Intended?
6. Given WRK-22, is there appetite to evaluate `Microsoft.CodeAnalysis.ExternalAccess.HotReload` --
   which brings Features -- as the hot-reload route, or is reimplementing EnC analysis on public API
   the intended path?
7. `SolutionLoader` sets `LoadMetadataForReferencedProjects = true`. Which repository needed it? It
   changes what a project reference means to EnC.

## Rose dogfooding notes

Every reach for a `rose_*` tool in this review, in order. The loaded solution was this worktree.

| # | Tool | For | Outcome |
|---|---|---|---|
| 1 | `rose_workspace_status` | Reproduce the Degraded report | Worked. The `degradedReasons` text ("One file name carrying a count is several versions of one generator colliding") was right about the shape; diagnosing *why* two versions collide (WRK-08) needed the loader source plus the targeting pack on disk. The report could name the two paths it tried to load; `analyzerLoadFailures` names only the file. |
| 2 | `rose_outline symbol=RoseMcp.Worker.MemberEditService includeDocumentation=false` | Map of a 1338-line file before reading it | Worked; 45 members. Verbose: every member carries a full `location` object with absolute path, `preview`, and `containingMember` -- which for a declaration is always the member itself. Still read the file in full because a review needs bodies; the outline was the right first call. |
| 3 | `rose_find_references symbol=RoseMcp.Worker.Whitespace.Dominant` | Count who reads the dominant line ending | Refused: two overloads, both listed with lines, and told to name parameter types. Correct behaviour. The example given (`Type.Member(int, string)`) did not say that a short type name (`Dominant(SourceText)`) is accepted; I wrote the fully qualified one to be safe. |
| 4 | `rose_find_references symbol=RoseMcp.Worker.Whitespace.Dominant(Microsoft.CodeAnalysis.Text.SourceText)` | Same, retried | Worked; 9 references with `containingMember`. Beat grep: grep gave me files, Rose gave me methods, which is the unit the finding (WRK-03) is about. |
| 5 | `rose_find_references symbol=RoseMcp.Worker.Whitespace.RulesFor` | Count independent rule computations | Worked; 15 references in 9 methods. Beat grep for the same reason. |
| 6 | `rose_symbol_info symbol=RoseMcp.XamlDiff.XamlDiff` | Reproduce #210 | Reproduced: "'XamlDiff' declares no constructor ... Add one with rose_add_member". Defect (WRK-05). |
| 7 | `rose_symbol_info symbol=RoseMcp.Contracts.SymbolLocation.ModuleSimpleName` | Reproduce #233 | "Nothing in the solution is called 'ModuleSimpleName'" -- correct, the property was renamed since the issue (by position, per the issue). The refusal sent me to `rose_search_symbols`. |
| 8 | `rose_search_symbols query=SymbolLocation` | Find the record's current members | Worked, but 2 of 4 results were TUnit-generated registration types (WRK-21). Then I used `grep` to list the record's positional parameters rather than `rose_outline symbol=RoseMcp.Contracts.SymbolLocation` -- **a tool not reached for when it should have been**; habit, not a Rose defect. |
| 9 | `rose_symbol_info` and `rose_find_references symbol=RoseMcp.Contracts.SymbolLocation.TypeName` | Reproduce #233 on a positional record property that still exists | **Both failed**: "Nothing is declared at ... 'TypeName' is declared as [15 other types].TypeName". Broader than #233: the read side fails too whenever the name is not globally unique, and the metadata fallback is not taken (WRK-04). |
| 10 | `rose_find_implementations symbol=RoseMcp.Worker.IWorkProgress` | Who implements progress | Worked; 5 implementations including private nested classes and the test double. Grep would have found `: IWorkProgress` too, but not distinguished nested from top-level. |
| 11 | `rose_diagnostics project=RoseMcp.Worker` | Confirm the worker compiles clean | Worked; 0 at warning+, fast. |
| 12 | `rose_project_graph project=RoseMcp.Worker` | Dependencies for the loading section | Worked. `targetFramework` omitted for a single-target project by design; fine. |
| 13 | `rose_find_references symbol=RoseMcp.Worker.SymbolLocator.FindDocument` | Callers of the O(documents) lookup | Worked; 4 references. My grep had counted `RequireDocument` too. Rose was more precise (WRK-16). |

Where grep was used instead of Rose, and why: cross-cutting *textual* patterns -- `catch (Exception`,
`"\r\n"` literals, `IndentAt(` definitions, history words in comments, `new WorkspaceSnapshot` --
have no semantic tool and are legitimately grep. Reading whole files was deliberate for a review.
Net: Rose won every semantic question it was asked (references by containing member, implementations,
diagnostics) and lost on two addressing defects it exists to not have (#210, and the positional-record
case in WRK-04), both now filed here with repros.
