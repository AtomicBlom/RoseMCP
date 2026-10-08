# What a result may say

Read before adding a tool, adding a field to a result, or changing an error path.

- **An error says what went wrong, not that something did.** The SDK replaces the message of any
  exception it does not recognise with "An error occurred invoking 'rose_rename_symbol'." A
  call-tool filter at each MCP boundary forwards the real message instead, and the worker adds the
  solution it owns. Convert at the boundary, never at the throw sites: the exception type carries
  meaning further in -- services separate a caller's mistake from an impossible state, the manager
  separates either from a dead worker, and retry decisions turn on that.
- **Every tool answers with a record, and the live-app prefixes excuse only workspace
  attribution.** A sentence names nothing a caller can check and gives an agent nothing to branch
  on but its wording: "closed" says which workspace to nobody holding two, and "detached" which
  session. A workspace's result derives from `WorkspaceScopedResult`, which the broker fills in one
  place. A live-app result answers about a debugged process, which belongs to no workspace and has
  no revision, so the `rose_debug_` and `rose_xaml_` prefixes excuse it from that and from nothing
  else: it is still a record, and a result that ends or resumes a session names the session, the
  one the caller passed, since a caller may hold several and one that is no longer open has nothing
  else to read it from. An outcome that is not a failure -- nothing was open, nothing was stopped --
  is a field, and an outcome that leaves something at risk, a detach that could not take the
  debugger off its target, is still an error rather than a field that can be read past.
  `ToolResultShapeTests` holds the record over the whole surface without consulting the prefixes,
  so the exemption cannot quietly widen into excusing prose.
- **An argument name the tool does not declare is said, on every call that carries one.** An
  argument name is part of a tool's vocabulary, so a name the tool does not know is a caller error
  the tool can see -- exactly as a wrong-shaped value is. The binder drops it rather than refusing it
  and binds the declared argument at its default, so nothing past the binder can tell it was sent:
  without this, `rose_outline(type: ...)` is refused for want of a type it was given, and
  `rose_find_references(symbol: ..., projet: ...)` searches every project and answers a question
  nobody asked. `ToolArgumentShape` reads the schema once for both halves: a refusal gains a
  sentence naming the argument and the nearest declared name, at every MCP boundary; a call that
  succeeds gains a notice saying the same, in its `notices` -- added to a result type that has none,
  since the listing carries no output schema and one name for one kind of remark is less surprising
  than two. The notice is the broker's alone, after the alias filter: it is the only process that
  sees what the caller sent, and a spelling it accepts in place of a declared name is not unknown.
  Named, never refused, and never answered with a further alias -- clients attach extras of their
  own, and an alias teaches nobody the real name.
- **A result's text is written with one encoder, `ToolJson.Encoder`, in every host.** A typed
  tool's answer reaches a client twice, as structured content and as a text block holding the same
  JSON, and most clients hand the model the text. The SDK writes that text with the framework's
  default encoder unless a registration passes options, and the default spells every `+ < > ' "`,
  backtick and `&` as a six-character escape inside the string, where no client decodes it: a diff
  becomes unreadable and several times longer. So every `WithTools` and `WithToolsFromAssembly` passes
  `ToolJson.Readable(McpJsonUtilities.DefaultOptions)`, and anything that rewrites a result's text
  block afterwards -- the ignored-argument notice is one -- writes it with the same options: the
  broker keeps one instance for its registrations and its filters. A registration that leaves the
  options off, or a filter that picks its own, is a second spelling of the same answer that changes
  from one call to the next with nothing in the call saying why. The broker's and the worker's
  registrations are each held by a test; the live-app host's is held by review, since its text block
  is read by nothing but the broker, which reads only structured content, and reaching it directly
  takes a debugged target.
- **Advice names an argument only where the tool takes it and it reaches.** A write that leaves a
  name unresolved suggests the import, and a suggestion is followed literally: telling the caller of
  a tool without `usings` to pass it sends an argument the tool never sees, and telling
  `rose_change_signature`'s caller to pass it for a name left at a call site sends them round to the
  same error, since its `usings` go only where a declaration changes. So `EditPipeline.VerifyAsync`
  takes the files the tool's own `usings` reaches, with no default for a new tool to inherit, and
  `MissingImports` names `usings` only for those. Anywhere else it names `rose_add_using` and every
  file the name failed in, each by its whole path, since that is what `filePath` is matched against
  and a name answered once for its first file leaves the others failing after the advice is taken.
- **A name matching two symbols is refused, and the address a result hands back resolves.** These
  are the two halves of addressing code by name, and each fails by producing a well-formed answer
  about something else. A resolver keyed on a candidate's name, containing type and assembly
  collapses every overload of a method into one, so `System.IO.File.WriteAllTextAsync` resolved to
  whichever overload the enumeration reached first and `rose_find_references` answered that nothing
  called it -- a confident zero, with nothing in it saying the question had been ambiguous. What
  separates overloads is their parameters, so the key carries them and the refusal lists every
  candidate. Then what it lists has to parse, which is the other half: `SymbolAddress`'s format
  exists so an address read out of one answer can be handed to the next call, and dropping a
  parameter's type arguments spelled `Scan(System.ReadOnlyMemory)` for a method taking
  `ReadOnlyMemory<char>` -- naming a type that does not exist, and matching nothing. Type arguments
  come off the path, where a caller should not have to know how a declaration spells its type
  parameters, and stay on parameter types, which are what tell two overloads apart.
- **Every tool resolves a name through `SymbolResolver`, against the compilation.** A second resolver
  answers some address differently, and which tool a caller met first decides what it learns about
  the grammar: a positional record property was readable and not renameable, and a type named for its
  namespace reachable by neither. See
  [the decision](../decisions/a-name-is-resolved-by-the-compilation-in-one-place.md).
- **A read falls back to metadata when source declares nothing the address reaches, and only
  then.** Both edges of that condition are a wrong answer. Narrowed to "the name is carried nowhere
  in the solution", the fallback never fires for any library symbol whose last segment something
  here also declares: `Microsoft.CodeAnalysis.Document`, `System.Collections.Generic.List` and
  `ISymbol.Name` were all refused on this repository, each answered with unrelated source members
  in other namespaces as though they were near misses -- so the larger a solution grew, the more
  library symbols it refused. Widened to cover every refusal, it answers from a referenced assembly
  while source did reach something, which is a confident answer about somebody else's class: an
  ambiguous match is several declarations here, a declaration ruled out by where it lives was still
  found, and a type declaring no explicit constructor is still the type the caller meant.
  The resolver decides it in one place, filling its metadata answer only when source reached
  nothing, and `SymbolNotFoundException` marks exactly the refusals where that held -- saying, on a
  read, that referenced assemblies were searched too. A lone segment reaches metadata through the declaration index, because no metadata name
  lookup finds `StringBuilder` spelled that way and the source search has always accepted a bare
  last segment -- demanding the namespace only of metadata is strictest where the caller knows
  least.
- **A project a caller names is refused when nothing carries it, never widened and never emptied.**
  Both wrong answers are well-formed: the whole solution's generated documents reported as one
  project's, or an empty reference list that reads as a symbol nobody uses and invites a deletion.
  `ProjectNames` is the one way a tool turns a `project` argument into projects -- by name ignoring
  case, by a multi-targeted project's name without its framework, or by the path to its project
  file -- and its refusal lists the names there are. `ProjectNamesTests` fails any other worker type
  that compares a project's name with a string itself.
- **Every facet a result returns is a filter the tool owes, and an overflow is answered with a
  smaller question, never a bigger artefact.** A fact computed on every item is a fact a caller wants
  to select on; one it can read and not ask about leaves two ways to narrow a large answer, reading
  all of it or text-searching it, and the second throws away the precision the semantic search was
  paid for. `ProducedFactTests` fails a facet with no argument of its name. Past its cap,
  `rose_find_references` returns the shape of its references instead of the first few in path order
  -- counts by project, test project, generated code and member, each group keyed by the value its
  narrowing argument takes -- because a first-N cut is an arbitrary sample that reads as the whole
  answer. A filter that keeps nothing says so and describes every reference instead, since an empty
  list reads as a symbol nobody uses. `truncated` means one thing, that raising the cap lists more,
  so an answer that lists nothing because it was asked to is not truncated. Nothing spills to a file
  the caller did not name: a read that writes to disk unasked leaves files nobody owns, and telling the
  caller to grep them concedes the reason the tool exists.
- **Status may not report a field it cannot fill.** `GetStatusAsync` once passed `restore: null`,
  `loadSeconds: 0` and no load diagnostics, hard-coded, so every status answer on every solution
  carried the same three blanks. That is worse than omitting them: a failed restore reaches
  `degradedReasons` only through the restore report, so the workspace called itself healthy in
  exactly the situation it exists to warn about. Equally, do not report a signal that cannot mean
  what it says -- `targetFramework` was read from the project name, which carries a TFM only when a
  project multi-targets, leaving a permanent false alarm on the field that flags a wrong
  configuration. And whether a project's semantics can be trusted is asked of the compilation, never
  of MSBuild's chatter: MSBuild raises a `Failure` when NuGet's vulnerability audit cannot reach its
  feed, which names every project it could not audit and says nothing about whether they compiled.
  <br>
  The corollary decides result *types*, which is where it gets applied wrongly. A call that answers
  before a load has finished cannot fill a revision, a project list or a load-diagnostic list, and
  MCP gives a tool one output schema -- so `rose_workspace_open` returns a `WorkspaceSummary` and
  `rose_workspace_status` a `WorkspaceStatusReport`, rather than one shape with the awkward half
  nulled out. **Say it with the state, not with a null.** `WorkspaceState.Loading` is a fact about
  the workspace that implies a next action; a null field says only that something is unknown, and it
  can be missed in a way a required discriminator cannot. It is also the only shape carrying the
  activity log's percentages, so a poll can watch a load rather than merely wait for it. Do not
  reintroduce a second tool for this: `rose_workspace_open` was `rose_workspace_status` under another
  name, down to the same two lines of body, and not waiting is what gives it something to be.
- **A batch answers each entry, and one entry's mistake is that entry's status.** A tool that takes
  a list of independent requests -- tracepoints, breakpoints, ids to remove -- answers
  with one entry per request in the order given, each with a `status` that is the outcome or the
  reason there was none, so a caller can match an answer to what it sent without counting. Refusing
  the whole call for one bad entry sends the caller back to retry the good ones piece by piece, which
  is the turn count a batch exists to save; only what makes the call impossible as a whole is an
  error, and an empty list is one, since an answer with no entries reads as a call that worked. An
  entry that is waiting rather than wrong -- a breakpoint whose module has not loaded -- is a success
  that says so, never a refusal -- and one that will never bind, because the loaded module cannot
  carry it, says that instead, with why, since a caller told to wait waits for nothing. See [the decision](../decisions/a-plural-intent-is-one-call.md).
- **A fact about the load travels with the load.** Status re-describes the live snapshot on every call,
  so anything learned once per load -- the restore, the load diagnostics, the projects the worker's own
  MSBuild could not evaluate -- reaches a later status only through `LoadOutcome`, which a reload
  replaces. Passed to the load's report and not to `LoadOutcome`, it is said once and then the workspace
  reads `Loaded` again with nothing changed.
- **A project naming an SDK that the worker cannot evaluate degrades; a legacy one is a notice.** The
  design-time build runs in Roslyn's build host, a process of its own, so a worker whose MSBuild has lost
  its SDK still loads every project and reports nothing else wrong. An SDK project is exactly what the
  SDK's MSBuild exists to evaluate, so its failing here is this process going wrong. A project naming no
  SDK fails here by design -- its targets ship only with Visual Studio's MSBuild -- and calling that
  degraded would mark every UWP solution degraded.
- **An assembly the worker's own code cannot load is a fact about the worker, and status keeps it.** A
  framework or Roslyn assembly that fails to load once fails on every later call that reaches the same
  code, while the tools that do not reach it answer normally -- so the workspace reads `Loaded` while two
  tools are dead, and the loader's message names a file and nothing else. The worker's call-tool filter
  reads it off the exception (`AssemblyLoadFault`), says what it means and that `rose_workspace_reload`
  starts a fresh worker, and records it on the session, which status reads on every call and a reload does
  not clear. A missing source file throws the same exception type naming a path, and an analyzer's own
  dependency fails in a load context of its own; neither is this, and telling a caller to restart the
  worker over them would be wrong advice.
- **A fixer that declines is the same as no fixer.** `rose_list_code_fixes` dropped a diagnostic
  whose providers offered nothing from `fixes` and from `unfixableIds` both, so it disappeared from
  the answer entirely -- which is exactly what the second list exists to prevent. CS0103 is what
  found it: two fixers claim it and neither had anything to say.
- **A stale build output is a notice, never a degraded reason.** `Degraded` means these answers
  cannot be trusted, and they are exactly as good with a stale `bin` as without one, because they
  come from source. It is also the ordinary state of a solution somebody is editing, so putting it
  in `degradedReasons` would mark almost every workspace on the machine degraded -- the same
  emptying of the word that narrowed the MSBuild-failure count and took `targetFramework` out of the
  project name. It is still said, because what it warns about does not present as a build failure:
  it presents as a test failing for a reason that has nothing to do with the change.
- **What every item of a list shares is said once, and a narrowing says what it left out.** An
  outline that repeated its file, project and source line on every member cost ten times what the
  member names did, and on a large type overran what a client accepts -- so the caller read the file,
  which is the read the tool exists to replace. Put a field that is constant across a result's items
  on the result, or on the thing the items belong to, and leave out a flag an item does not have.
  The other half is the one a cap or a filter breaks: an empty or short list reads as the whole
  answer, so whatever narrowed it carries a total and a notice. `ResultBudgetTests` holds the per-item
  cost. See [the decision](../decisions/an-outline-is-cheap-by-default.md).
  <br>
  `rose_find_references` lists its references by file for the same reason: the path, the project and
  whether it is a test project are said once per file rather than on every hit.
  <br>
  A write's `changedFiles` is the one list a narrowing must not reach early: `WorkspaceManager` reads
  all of it to say which sibling solution compiles the same files, so a worker that cut it would hide
  a sibling whose files fell past the cut. Where a tool names fewer, as `rose_replace_pattern` does past
  twenty, the broker cuts it after the manager has answered.
