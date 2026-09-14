# What a result may say

Read before adding a tool, adding a field to a result, or changing an error path.

- **An error says what went wrong, not that something did.** The SDK replaces the message of any
  exception it does not recognise with "An error occurred invoking 'rose_rename_symbol'." A
  call-tool filter at each MCP boundary forwards the real message instead, and the worker adds the
  solution it owns. Convert at the boundary, never at the throw sites: the exception type carries
  meaning further in -- services separate a caller's mistake from an impossible state, the manager
  separates either from a dead worker, and retry decisions turn on that.
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
