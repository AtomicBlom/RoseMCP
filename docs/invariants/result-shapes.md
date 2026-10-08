# What a result may say

Read before adding a tool, adding a field to a result, or changing an error path.

- **An error says what went wrong, not that something did.** The SDK replaces the message of any
  exception it does not recognise with "An error occurred invoking 'rose_rename_symbol'." A
  call-tool filter at each MCP boundary forwards the real message instead, and the worker adds the
  solution it owns. Convert at the boundary, never at the throw sites: the exception type carries
  meaning further in -- services separate a caller's mistake from an impossible state, the manager
  separates either from a dead worker, and retry decisions turn on that.
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
  A write's `changedFiles` is the one list a narrowing must not reach early: `WorkspaceManager` reads
  all of it to say which sibling solution compiles the same files, so a worker that cut it would hide
  a sibling whose files fell past the cut. Where a tool names fewer, as `rose_replace_pattern` does past
  twenty, the broker cuts it after the manager has answered.
