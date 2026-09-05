# RoseMCP

An MCP server that gives coding agents real Roslyn semantics over a loaded C# solution:
diagnostics, navigation, source-generated code, and refactorings.

It is built using itself -- see **Dogfooding is the point** below, which is the development method
and not a slogan.

It exists to fix three things that other Roslyn MCP servers get wrong:

1. **Stale diagnostics.** The agent edits files with its own tools, the workspace never learns,
   and answers come from an old snapshot. Here, every read is ordered behind every pending
   mutation *and* behind a disk-reconciliation barrier.
2. **Source generators silently producing nothing.** `Microsoft.CodeAnalysis.Workspaces.MSBuild`
   runs a design-time build and reads `CscCommandLineArgs`, so analyzers and generators arrive
   exactly as the real compiler sees them -- but only if restore has run and any in-solution
   generator project's output DLL already exists on disk. We check for that and report it loudly
   instead of returning an empty list.
3. **Reloading the solution on every operation.** One warm worker process per solution.

## Architecture

```
client --stdio or http--> RoseMcp.Server (broker, no Roslyn refs)
   or RoseMcp.Tray    -->    |  one child per solution, MCP over stdio
   (hosts it in-process)     +--> RoseMcp.Worker --solution D:\a\A.sln
                             +--> RoseMcp.Worker --solution D:\b\B.slnx

client --stdio--> RoseMcp.Server --http--> RoseMcp.Tray --> the tray's workers
                  (TrayRelay, no workers of its own)
```

- **`RoseMcp.Contracts`** -- DTOs and tool-name constants shared by broker and worker.
- **`RoseMcp.Solutions`** -- library. Reads solution files and `rosemcp.json` without MSBuild or
  Roslyn, so the broker can decide *which* solution a call means without taking a dependency on the
  thing that loads one. Also derives the short workspace key.
- **`RoseMcp.Logging`** -- library. The file sink, referenced only by the three launchable hosts
  so Serilog stays off the DTO assembly and the tests.
- **`RoseMcp.Broker`** -- library. `WorkspaceManager`, worker supervision, the tool layer, the
  activity log, and `AddRoseMcpBroker()`. One registration path, used by both hosts below.
- **`RoseMcp.Server`** -- console host. `--transport stdio` (default) or `--transport http`.
- **`RoseMcp.Worker`** -- owns exactly one `MSBuildWorkspace`. All Roslyn work happens here.
- **`RoseMcp.XamlStubs`** -- the XAML stub generator, loaded by the worker as an analyzer assembly
  rather than referenced as a library.
- **`RoseMcp.Tray`** -- WinUI 3 tray app for http mode. Hosts the broker in-process, so its
  window reads the live `WorkspaceManager` directly rather than through an API.

The worker is a separate process because analyzer and generator assemblies cannot be unloaded
once loaded, MSBuild resolution is per-process, and killing a worker is the only reliable way to
reclaim memory or pick up a rebuilt generator.

### Invariants worth protecting

- **Nothing writes to stdout in stdio mode** except protocol frames. All logging goes to stderr,
  and to a file. A stray `Console.WriteLine` corrupts the stream, and the failure looks like a
  protocol bug. `RoseMcp.Logging` adds the file sink -- Serilog behind the existing
  `Microsoft.Extensions.Logging` call sites, never a console sink, and there is a regression test
  asserting the pipeline writes nothing to stdout at all. Logs land in
  `%LOCALAPPDATA%/BinaryVibrance/RoseMCP/Logs/{Server,Worker,Tray}/[{solution}-]{yyyyMMdd-HHmmss}.log`
  -- under their own `Logs` folder, separate from the install that shares the same vendor/product
  parent, so promoting a build never touches a session's log files. UTC in the name and UTC in
  every line so the two cannot disagree. A worker's file names the
  solution it owns, hashed as well as spelled, because two worktrees of one repository share a
  solution name. Twenty sessions are kept per component, pruned at startup -- Serilog's own
  retention cannot do it, since it only prunes within one rolling base name and every session
  here has its own.
- **A stdio session relays to a tray when one is running.** `TrayRelay` forwards both listing and
  calling, declaring no tools of its own, so the surface cannot drift from the tray's. It sends the
  directory its client started it in as `_meta["rosemcp/originDirectory"]` and changes nothing else.
  An http broker serves every repository on the machine and, with two solutions open, cannot know
  which one a bare call means; a stdio process cannot not know. Relaying buys both that and one warm
  worker per solution shared across sessions, and it keeps the tray's window reading a live
  `WorkspaceManager` rather than a pushed copy that could drift. With no tray, the same process owns
  its workers as before.
- **The relay contributes a fact, never a conclusion.** It used to resolve that directory to a
  solution and write the answer into the `workspace` argument, which failed three ways at once. It
  matched the argument by name, and `rose_workspace_open` alone called its own `path` -- so an
  explicitly named solution was read as an omission and refused for an ambiguity the caller had
  already settled. It resolved before reading any argument, so a call carrying a `filePath`
  containment would have decided was refused too, killing every `rose_*` tool in a multi-solution
  root. And it ran for every tool, so `rose_debug_launch_uwp`, which takes no workspace and needs no
  solution, failed with a solution-ambiguity error. Resolution belongs where every path converges --
  the broker also serves clients with no relay in front of it -- so the relay sends what only it
  knows and the broker ranks it against everything else.
- **A cancelled call is cancelled all the way down, and the order is the trick.**
  `McpClient.CallToolAsync` honours a token by giving up locally and never telling the far side, so
  a cancelled analyzer run kept its worker busy five seconds longer -- which, because reads are
  ordered behind one another, is the delay before the caller's next question can start.
  `CancellableToolCall` builds the request itself, since the id it needs is one `CallToolAsync`
  never reveals. Send the cancellation *before* abandoning the wait: over http the request is a
  streaming POST, and tearing it down three milliseconds early was enough for the far side to treat
  the request as merely gone, never cancel its own token, and finish the work anyway.
- **Progress can arrive out of order over http, and no queue here can fix it.** The SDK dispatches
  notification handlers concurrently on an SSE transport, so a four-project status was seen
  arriving 50, 75, 0, 25 -- already unordered before any of our code sees it, and MCP progress
  carries no sequence to sort on. Do not "fix" it by clamping to the highest value seen:
  `SharedWorkProgress` deliberately fans a reload's own scale into calls already in flight, so
  resets are legitimate and a clamp pins those bars at whatever the last operation reached. The
  stdio hop is ordered, being one reader.
- **Reads never observe a snapshot older than disk.** If you add a read path, it goes through the
  `WorkspaceSession` barrier. No exceptions.
- **A file that appears is absorbed on the next read, not waited for.** A file created on disk is
  part of the state of disk, and the stat sweep cannot find one -- it checks the documents it already
  knows. So a new `.cs` file used to be invisible until something forced a reload, and every
  reference to the new type reported `CS0103` against perfectly good code: not an error but a
  confident answer about a file that is not there, which is the worst shape of failure here and the
  one that became likely the moment an agent could write C# rather than only read it. The barrier
  walks the project directories, pruned of `bin`, `obj` and dot directories, rather than trusting the
  watcher -- a watcher event lands some milliseconds after the write and an agent asks immediately,
  so trusting it would make the answer depend on a race, which is worse than a slow answer because it
  is intermittent and still confident. Containment in a project's directory is the attribution,
  because that is exactly what the SDK's default globs compile; a project whose own text lists its
  files instead has nothing claimed for it and the file is reported as not being in the build. The
  watcher's list of appearances is still used for one thing: a project or build file appearing, which
  no snapshot can represent and which sends the session round a reload.
- **Every result carries a `revision`.** It is how callers detect that the world moved.
- **A directory with two solutions is never resolved by guessing.** `SolutionResolver` used to sort
  by name and take the first, which in `D:\Drawboard\Revit` is a one-project installer sitting beside
  the seventeen-project solution everyone means -- so every bare call answered from the wrong
  compilation and returned nothing, shaped exactly like a true negative. Containment decides it: the
  solution that compiles the path you named is the one that can answer about it, and reading a
  project list is a parse, not a build. A repository root encloses no project, so it reaches the tie
  with nothing to go on -- that is an error naming the candidates, and a `"solution"` entry in the
  directory's `rosemcp.json` settles it durably. Refusing is against the grain of everything else
  here, and earns it because guessing is not cheap: the wrong guess pays a full design-time build of
  a solution nobody asked for.
- **Containment narrows, then a pin breaks the tie -- in that order.** `Disambiguate` used to check
  the pin first, contradicting its own docstring and the name of the test covering it, which only
  ever resolved a bare directory and so passed either way. It matters because the pin is the fix the
  ambiguity error recommends: `D:\Drawboard\Windows\Windows.IntegrationFramework` holds three
  solutions at its root and the largest omits 23 projects under `Pdf/` and 5 under `Shared/`, so
  taking that advice pointed every question about those 28 projects at a compilation not containing
  the file. A pin is an ambient default for the directory; containment is evidence about the path in
  hand, and evidence wins. The pin still decides a bare directory, and still decides between several
  solutions that all compile the path.
- **One ordering decides which workspace a call means, in `WorkspaceManager.WorkspaceFor`.** The
  workspace argument, then paths the call carries, then the calling session's directory, then refuse.
  It was previously spread across three places that disagreed, and the worst of them was the last
  resort: with no other signal the broker answered from the single loaded worker, which is a fact
  about what another session did earlier rather than about the question, so a call in one repository
  could be answered plausibly and silently from another. Loaded workspaces are named in the failure
  and never used as an answer. Tools declare their inputs as `WorkspaceHints` and no longer spell the
  ranking out themselves -- seventeen hand-written `workspace ?? filePath` chains had already drifted
  to one `workspace ?? filePaths.FirstOrDefault()`, and one of those hints is not even a path
  (`rose_diagnostics`' `target` is a project name under project scope), so a hint naming nothing on
  disk is passed over rather than resolved relative to the process directory.
- **Every result names the workspace that answered.** Attribution is added once, in
  `WorkspaceManager`, so a tool added later cannot forget. The key is derived from the path rather
  than minted per process -- workers are replaced routinely, and a key that died with one would tell
  a caller its workspace was gone when nothing had changed. Spelled *and* hashed, because six
  worktrees of one repository is the ordinary case and each holds a solution of the same name.
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
- **A change that reaches another solution says so.** Roslyn renames within one `Solution` and writes
  to disk, where every other solution over the same projects picks the new text up at its next read
  while still calling the old name from projects the renaming solution never had. That sibling is
  not stale, it is broken. Mutations report which solutions beside them compile the files they
  touched. Reported, not acted on: making a change complete across solutions means loading them all
  and merging the edits, and that is not always even well defined, since two solutions can build one
  project under configurations with no setting in common.
- **Analyzer assemblies are never loaded from where they live.** They are shadow-copied first. A
  loaded assembly is held open for the life of the process, and this process lives for hours, so
  loading them in place means the user cannot rebuild their own generator -- `dotnet build` fails
  with MSB3021. There is a regression test for this; do not "simplify" it away.
- **Whatever writes C# has to end formatted.** Roslyn's formatter honours `.editorconfig` but only
  rewrites the trivia it has reason to touch, so a file it reindents comes out with mixed line
  endings -- which IDE0055 then fails the build over. `Whitespace` is the second pass that fixes
  every line, and it leaves multi-line verbatim and raw literals alone, because a newline in one is
  content and a raw literal's indentation decides how much is stripped from it. Both passes take a
  span when the caller wrote one member rather than a file: a repository whose endings are already
  inconsistent would otherwise have every line rewritten by a one-member change, which buries the
  edit in a diff nobody can review.
- **A change a diff cannot show is said in words.** A unified diff compares the content of lines,
  and a terminator is not content -- so rewriting a file's endings produces no hunk at all. That is
  the change `rose_format` is called for most often, in exactly the repositories where it matters:
  where IDE0055 is an error, an LF in a CRLF file is a failed build, and fixing it is the whole
  reason the call was made. Reporting five changed files beside an empty diff reads precisely like a
  call that did nothing. So `SolutionWriter` counts the lines that moved and every writing tool
  passes the sentence on, rather than the alternatives: a whole-file hunk nobody can read, or
  inventing a hunk header that is not a patch.
- **Nothing writes code it has not parsed, and nothing is addressed by position.** The three write
  tools resolve the declaration and parse the code *before* the file is opened, so a refusal costs
  nothing and can never leave a file half-written -- which is most of the value, since it removes
  every failure a text splice produces by construction. Parsing happens inside a synthetic container
  of the same kind as the real one, because a member only means something in a container: a bare
  snippet parsed as a compilation unit turns `void M() { }` into a top-level local function, which
  parses cleanly and means something else. The shape is then checked as well as the syntax, because
  code that closes the container early and opens one of its own has no parse error at all and would
  smuggle a type into the file at top level. And a member is named, never pointed at: a line and
  column has to be found by reading the file first and is wrong the moment an earlier edit lands,
  which is how a text edit path produces an anchor found in the wrong place. Where a name matches
  more than one declaration it refuses and lists them, because writing correct code into the wrong
  overload is the only failure with no symptom at all.
- **Written code is indented for where it goes, because the formatter only does half of it.** Roslyn
  reindents statements and moves braces -- rules it has -- so a line wrapped by hand *inside a body*
  comes out right. A wrapped parameter list is layout it has no rule about, so it keeps whatever
  indentation arrived, and neither IDE0055 nor `dotnet format` says a word because neither of them
  has an opinion either. Code written for column zero therefore landed a level short of its
  neighbours, silently. `MemberSyntax` takes the code's own baseline indentation off every line and
  puts the destination's on: both halves, because a caller that has read the file and indented for
  the destination is as likely as one that wrote at column zero, and only removing the baseline
  first makes those the same request. Where the formatter *does* have a rule it still wins, since it
  runs afterwards. Replacing a body has the same trap from the other side: the signature is copied
  out of the file, and the span it is copied from begins *after* the indentation of its first line,
  so a wrapped parameter list read that way looks written at column zero and every continuation
  came out a level deep. Nothing downstream corrects it and nothing complains, so the signature
  drifted on a change that promised to touch only the body.
- **The line endings inside a string literal are content, and this was measured.** A raw literal
  written with CRLF and the same one written with LF are different strings -- the compiler says so,
  which is worth knowing because it is tempting to assume raw literals normalise and they do not. So
  nothing rewrites them, and `Shift` keeps each line's own ending rather than splitting on newlines
  and joining with one, which would have changed values inside the literals it was carefully not
  re-indenting. The consequence is reported rather than left silent: a multi-line literal written
  with endings the file does not use fails `dotnet format` while no build complains, and the obvious
  fix changes what the program says.
- **A signature change moves the whole declaration group, or it does not compile.** A virtual
  method whose override keeps the old parameters is a build error, and so is an interface member
  whose implementations keep theirs -- so `rose_change_signature` changes the member, its base
  declaration all the way up, the interface members it implements, and everything overriding or
  implementing those. Only the declaration the caller named gets the parameters they wrote; the rest
  are mapped by position and keep their own parameter names and attributes, because an override is
  free to call its parameters something else and replacing its list wholesale would rename them
  without saying so. Reordering existing parameters is refused rather than attempted: an argument's
  meaning at a call site is not always recoverable from its position. And the call sites that still
  compile are reported, because a forwarder that goes on passing the old default is the bug that
  hides -- "compiles" and "correct" part company exactly there.
- **An import goes where the file would have put it, and is refused when it is already in scope.**
  Writing a member is not the whole job: the code routinely needs an import the file has not got,
  and a tool that reports that and stops has handed the caller back to the text editing it was meant
  to replace, at the moment they had just been talked out of it. Placement is read from the file
  rather than from `.editorconfig` -- this repository sets
  `dotnet_separate_import_directive_groups = false` and every file separates its groups anyway,
  because the setting only stops the analyzer insisting -- and going in first means inheriting
  whatever sat above the old first line, since a licence header under an import changes what the
  file means to other tools. Whether it is needed is asked of the *compilation*, not of the import
  block: a global using, an implicit using from the SDK, and the namespace the file is in are all
  ways to be in scope without appearing there, and importing one of those again is IDE0005. Both
  halves of getting it wrong are build errors, which is the only reason it is worth this much code.
- **Which namespace a name needs is a search, and it refuses to pick.** Being told is the common
  case by a distance -- someone writing `Encoding.UTF8` knows it is `System.Text` -- and the rest
  is a question the compilation can already answer, since it holds every type in every referenced
  assembly. What it must not do is choose. Plenty of names live in two namespaces at once, and the
  wrong import is the worst shape of wrong here because it compiles and binds to the wrong type, so
  the answer is one namespace or a list, never the first of several. It is as careful about the
  things that look like an answer and are not: a nested type reachable only through its container,
  an arity that does not match the use site, a type in a project this one does not reference, and a
  namespace already in scope. Each of those turns adding the obvious using into a second error
  rather than none. Most of the value is in never being asked -- the write tools run the search
  over the errors they introduced, off the compilation they had just built to find them, so the
  namespace arrives with the error rather than a call later. The IDE's own add-import fix is no
  route to any of it: that lives in `Microsoft.CodeAnalysis.CSharp.Features`, which is not
  referenced, and what *is* registered for CS0103 offers to generate the missing member -- the
  wrong fix, confidently, for a name that exists already.
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
- **A solution is loaded under properties it declares.** MSBuild's `Debug|AnyCPU` default is not
  universal. Where `TargetFramework` is derived from the configuration name -- Drawboard's Revit
  add-in derives it from `Debug-2024` through `Debug-2027` -- the wrong configuration yields projects
  with no framework and no references, and thousands of diagnostics about `System.Object` being
  undefined. `BuildProperties` chooses: the caller wins, else the solution's declared list, else
  MSBuild's default untouched, and a `rosemcp.json` (or `A.slnx.rosemcp.json`) beside the solution
  pins it durably so no setup call is needed -- beside it and never up the tree, because two
  solutions in one directory routinely declare different configurations. Restore gets the same properties, because a repository that moves
  `BaseIntermediateOutputPath` per configuration moves its assets file with it.
- **A structural edit is applied against the live collection, never against markup's idea of it.**
  Two facts about `IVisualTreeService` cost real time to find and neither is visible in the
  signatures. What `AddChild` and `RemoveChild` call a *parent* is the **collection**, not the
  element: passing a panel's handle returns `ERROR_NOT_FOUND`, and what they want is the value of one
  of its collection-valued properties (`Children` on a `Panel`, `Items` on an `ItemsControl`). And
  `CreateInstance` takes a **null** value for an element, not an empty string -- an empty one asks the
  framework to parse `""` as a Grid and it answers `E_UNEXPECTED`, which reads like a bad type name
  and is not one. The two codes tell those apart: `E_FAIL` is "no type of that name", `E_UNEXPECTED`
  is a real type built wrongly.
  <br>
  The index is asked of the collection rather than taken from the diff, and that is not tidiness. In
  the test that proves this, the add runs first and inserts at 1, so the element the removal names has
  moved to 2 by the time it runs -- the markup index would have removed the element just added, and
  reported success. Removal also has to be *forgotten* from the node list, closed over descendants,
  because that list is append-only: `OnVisualTreeChange` appends on Add and removes nothing on Remove,
  so the next edit in the same batch would otherwise resolve against a tree that no longer exists.
  <br>
  Markup is taken apart in `RoseMcp.XamlDiff`, not in the host: the host cannot be unit tested, since
  it targets Windows and the test projects cannot see inside it, and the ordering this depends on --
  create, fill, nest, and attach to the running app *last*, so nothing can observe a half-built
  element -- is exactly the kind of thing that needs a test rather than a comment.
  <br>
  A `*.Resources` block is the same trap wearing a different hat. It is a property written in element
  form, so it is not a child: walking into it produced `Grid[0]/Grid.Resources[0]/SolidColorBrush[0]`
  and the apply then failed naming a missing element, which is the wrong problem stated confidently.
  It also must not occupy a child index, or an element added after a `<Grid.RowDefinitions>` is handed
  a position counting something that is not its sibling. Resources are matched by `x:Key` and never by
  position, and the whole resource is replaced rather than its properties edited, because one brush
  object can sit behind several keys. `Resources` itself is **not** in the property chain -- it is an
  ordinary property on `FrameworkElement`, not a dependency property -- so the dictionary is asked of
  the element through `GetIInspectableFromHandle`, and `ReplaceResource` wants a *key handle*, which is
  a boxed string put back through `GetHandleFromIInspectable`.
  <br>
  One thing about the fixture rather than the code, because it cost a crash to learn: XAML resolves
  `{ThemeResource}` and `{StaticResource}` at parse time against resources declared *earlier*, so a
  resources block placed after the element that uses it is a forward reference and the app dies on
  launch. And the reference has to be `ThemeResource` for a replacement to be observable at all --
  `StaticResource` resolves once when the tree is built, so replacing what the key means would change
  the dictionary and move nothing on screen.
- **A live element is addressed by one grammar, counted the same way at both ends.** An `x:Name` is
  absent far more often than not -- everything inside a control template is unnamed -- so an element
  is addressed as `#name` or, failing that, `Type[index]` segments anchored at its nearest named
  ancestor. Both halves have to count identically or the address resolves to the element next door,
  which is the worst available outcome here: the change lands, the status says `applied`, and the
  thing that moved is not the thing that was named. Three ways that went wrong are worth knowing.
  The diff counted siblings by their *qualified* XML name and printed the *local* one, so
  `local:Border` and `Border` each counted only their own kind and both came out `Border[0]` -- one
  address for two elements. The visual tree carries a CLR type name and no XML namespace at all, so
  the local name is the only part both ends can see, and that is what decides the count. And our own
  toolbar is excluded when the index is built, not only when the snapshot is written: an address is a
  position among siblings, so counting an element nobody can see shifts every address after it. A
  duplicate `x:Name` is refused rather than answered, because a template instantiated three times
  gives three elements of that name and picking one of them is a guess wearing a success message.
  Addresses computed from the live tree are exact, being resolved against the tree they came from; an
  address a diff derived from markup is a best effort, since markup order is not always the visual
  tree's, and it fails by saying so.
- **Reading an element's properties changes what the element reports about itself.** Walking the
  property chain brings a `TextBlock`'s untouched collection properties into existence, and a
  property that exists is no longer the framework's default -- so a second read reports `Inlines`,
  `TextHighlighters` and `SelectionHighlightColor` as `Local`, with provenance and values as
  plausible as the ones the markup really set. The first read of an element is the accurate one, and
  it is our own read that spoils it. Measured, including the part that decides the fix: the additions
  arrive as `Local`, so there is no source left to filter on and the one-line fix does not exist
  (#97). A `Border` is stable, so this belongs to the type's text properties rather than to reading
  as such. What follows is that `includeDefaults: false` means "what the framework calls set", which
  is not quite "what the XAML sets" -- and the tool now says so rather than implying an exactness it
  cannot deliver. **Do not "fix" it by caching the first read's names and filtering later reads to
  them:** that hides exactly what the apply-then-read-back loop exists to verify, since an applied
  property need not have appeared in the first read. It also means `rose_xaml_properties` is declared
  read-only and is not quite, though nothing the app draws changes.
- **One XAML request at a time, and the lock has to be re-entrant.** The live-app host serves MCP
  calls concurrently -- measured, not assumed: two tree reads issued together finished in 118ms
  against a warm single read of 112ms -- and every XAML request shares one work folder, one
  `request.txt` and one generation counter. Ten concurrent pairs against the probe produced three
  outcomes: a `request.txt` that could not be written because the other call held it, several
  fifteen-second waits for a snapshot the other call's injection had already consumed, and once a
  tree of 22 elements where the app has 24, returned with no detail set. The last is why this is a
  lock and not a documented limitation, since a truncated tree hands out handles for a tree that is
  not there. Serialised rather than given a folder each, because the provider keeps its work folder
  in a global and does everything on the app's UI thread, so two folders would need a different
  provider and would buy no parallelism from a single-threaded consumer. It must be re-entrant --
  `System.Threading.Lock`, which is what the rest of this codebase uses: selecting by handle finishes
  by calling `ReadSelection`, which takes the lock again on the same thread, and a `SemaphoreSlim`
  would deadlock that forever. Do not conclude from a passing
  concurrency test that the lock is unnecessary -- the silent failure appeared once in ten, and the
  test was confirmed to fail with the locks removed.
- **It is a live edit, not a hot reload, and the word is doing work.** Every edit is a property set or
  an `AddChild` against the element objects that exist at that instant; the app's compiled markup is
  untouched, so anything that rebuilds that part of the UI produces the original. "Reload" would
  promise that elements created later carry the change, and they do not. `WorkspaceReload` is also
  right there meaning the other thing, the one that genuinely reloads. So: the capability is a **live
  edit**, the operation is an **apply**, and an **edit** is the smallest unit -- `XamlEdit`,
  `LiveXamlEditResult`. Microsoft's own name for the mechanism is XAML Hot Reload, which is why the
  tool description and the server instructions say so once each: it is the reader's search term, not
  this project's vocabulary.
- **A live edit is diffed against what was last sent to the app, never against what is on disk.**
  Applying used to require both versions of the markup, which reads reasonably and is close to unusable
  in the loop it exists for: an agent that has just written a file no longer holds what was in it, so
  the one piece of state the session is in a position to keep was being asked of the caller. It keeps it
  now, per file, and a caller passes a path. Two consequences are load-bearing. A *first* apply cannot
  be diffed at all -- what the running app was built from is not on disk once it has been edited -- so
  it records the file and says so, rather than diffing the file against itself and reporting the empty
  result as success, which would skip the caller's first edit in silence. And the baseline advances
  whether or not every edit took, because a structural edit is not idempotent: re-sending an `AddChild`
  because something else in the batch failed puts a second copy of the element in on the attempt that
  works. Failures are reported and belong to the caller. Whether the file has changed since the app
  started is evidence about the file and nothing more, so it is three-valued -- a process that will not
  give its start time makes that unknown, not "changed".
- **A provider marker answers one request, and the generation is what says which.** Every handshake
  through the work folder is "does this file exist", the host deletes the marker before injecting, and
  `TryDelete` swallows its failures -- so the number the host stamps on the request and the provider
  echoes back is the only thing separating this answer from the last one (#57, #89). Two things about
  that are not mechanical. The tree snapshot is written *before* the request is read, so hoisting the
  read is part of the stamp: otherwise the marker carries the previous request's number and the host
  rejects a perfectly good tree as stale, which presents as a timeout blaming the app's diagnostics
  layer. And `selection.ready` deliberately carries no generation at all, because it records a click,
  which outlives the injection that armed select mode by design -- stamping it would have the read that
  goes looking for it reject its own answer. One file cannot both answer a request and survive one.
- **A XAML project's generated half is synthesised, and says so.** The markup compiler runs only in a
  real build, so `MSBuildWorkspace` hands us code-behind missing its base type, its `x:Name` fields
  and `InitializeComponent` -- 2030 phantom errors in one project of Drawboard's UWP app.
  `RoseMcp.XamlStubs` parses the markup and generates that partial. Element types resolve out of the
  Roslyn type universe; anything that does not resolve is left out and reported, never faked. Check
  changes against the `.g.i.cs` files a real build leaves in `obj` -- that comparison is what found
  the four things reasoning had missed, and it agrees exactly today.
- **Never hand Roslyn a custom `AnalyzerReference`.** Its serializer switches on the concrete type --
  `AnalyzerFileReference`, `AnalyzerImageReference`, and an interface nested inside an internal class
  -- and throws `Unexpected value` on everything else. It checksums a project's analyzer references
  whenever it builds the index behind `FindDerivedClasses`, which every member-level find-references
  and rename reaches through `FindImplementedInterfaceMembers`. The stub generator used to be an
  in-memory subclass, for the good reason that there was then no analyzer assembly to ship or
  version-match, and that made those two tools throw on every solution containing XAML -- while
  type-level searches, which never build that index, went on working and hid it. It is a real
  assembly now, loaded as an `AnalyzerFileReference` through the shadow-copying loader, which is
  also what keeps it rebuildable. Two tests in `XamlWorkspaceTests` fail with `Unexpected value` if
  anyone wraps it again; the fixture's `Greeter` exists only to force that walk. Roslyn constructs
  the generator itself now, so there is no callback to hand it -- it reports through one generated
  document, which `GeneratedDocumentService` hides and `XamlStubReportReader` parses.
- **Every slow path says where it has got to.** Workers report progress on the operations that take
  real time, the broker records every call it forwards in an `ActivityLog`, and `WorkspaceSummary`
  carries both the running and the recently finished ones. The tray window and
  `GET /admin/workspaces` read that same model, so they cannot disagree. A percentage is per
  operation and only ever rises; no percentage means "cannot say", which shows as an indeterminate
  bar rather than one frozen at a number that has stopped meaning anything.
- **A worker is asked for status the moment it connects.** Progress notifications only exist inside
  a request, but a worker starts loading when the process does. With no call in flight the first
  half-minute of a large solution is invisible -- which is exactly what a reload from the tray
  produces, since no client is waiting on it. The priming call pays for nothing the first real call
  would not have.
- **Workers die with the broker.** A worker exits when its stdin closes. Orphaned Roslyn hosts
  holding a solution in memory are invisible until the machine is out of RAM.

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
./tools/deploy.ps1                          # test, stop tray, publish, restart
./tools/deploy.ps1 -Mode package            # artifacts/rosemcp-win-{x64,arm64}.zip
```

`promote` installs to `-Destination`, else `ROSEMCP_DEPLOY_ROOT`, else
`%LOCALAPPDATA%/BinaryVibrance/RoseMCP` -- the same vendor/product folder the logs live under.
Where a machine keeps its install is that machine's business, so no path is committed here.

Tests are split by what they cost. `RoseMcp.UnitTests` touches no disk, no MSBuild and no child
process -- 253 tests in about a second, so it is worth running on every change.
`RoseMcp.IntegrationTests` loads real solutions from `tests/fixtures`, runs real design-time
builds and starts real workers, and takes about four minutes (236 tests). `RoseMcp.TestSupport` holds the
doubles both need. Put a test where its cost puts it: a test that needs a `FixtureSolution` or a
`TestSession` is an integration test however small it looks.

The live-app tests are the expensive part, and they are phased by what each one can share (D33, D35).
A launch of the UWP probe costs about 6.5 seconds and the XAML work in a test costs about 1.2, so the
question that decides the suite's growth is what a *new* test costs. Three ways to ask for the app,
and what each gives up is different -- "isolation" as one property is the wrong thing to reason
about:

- `TakeAppAsync` launches its own app. For tests where a fresh process is the subject.
- `TakeSessionAsync` takes the shared app to itself, for state with no owner smaller than the app:
  the pick, select mode, the resource dictionary. **It must hand the app back unselected and
  unarmed, and the turn fails the test that does not.**
- `TakeSlotAsync` shares the app and owns one named slot to build elements in. Most tests.

Slots are *named* because an unnamed element is addressed by position under its nearest named
ancestor, so two tests filling one container renumber each other's addresses. Two things they cannot
do: a slot-built element has no source info, because nothing declared it, and it is not pristine,
because creating it materialises collection properties.

**A slot has to be given back empty, and the turn fails the test that cannot do it** -- the same rule
as a phase B turn, for the same reason. Slots come off a stack, so the one just released is the next
one handed out: residue is picked up immediately by a different test, which counts elements it never
added and fails somewhere else entirely. That is how the last-first removal ordering was found (D36),
and it is worth knowing that the bug wore three costumes -- a removal reported applied that did not
happen, an element count off by one, and a test that passed alone and failed in company. Checking the
cleanup is what collapsed them into one.

Four rules hold the whole thing up, each of which cost a run to learn. **Toolchain work goes in the
assembly fixture, once, never per test** -- the native provider is 23 seconds every time however warm
it is. **Serialise the resource, not the class**: `[TestClass(DisableParallelization = true)]` reads
as "not in parallel with each other" and means "not in parallel with *anything*", which made the
suite's two halves add up instead of overlapping; `MaxThreads` does not bound async tests either.
**One gate for everything that touches the app** -- two locks over one single-instance app is not two
locks, it is none, and that rule keeps being applied one layer too high. Sequencing the *tests*
correctly is not enough if the thing they queue for can be rebuilt by several of them at once:
phase C tests overlap by design and each asks for the shared session, so after a phase A test ends
the app they all arrive together, all correctly see no app, and all start by ending the app before
launching it. Bringing the app up is the one thing a reader does that is not read-only, so it takes a
lock of its own (D36). And **a fixture check that can hang is worse than the bug it looks for**: bound
it, or a failing test becomes a wedged suite.

A fifth, learned later and the hard way: **green once is not green.** This suite was reported passing
off a single run and was in fact failing one or two of thirty-one, from three unrelated causes that
only repeats made visible (D36). A flake rate is a measurement like any other and needs more than one
sample. One of those three is worth stating as its own rule, because it is easy to write again:
**a fixture's timer has to outlast the whole suite, not one test.** `DebugProbeTarget` self-terminated
after 120 seconds, which was ample when a live-app test had the machine to itself and became wrong
the moment they shared it -- the tests that end by asserting their target is still running failed on
it having correctly done what it was told. It is ten minutes now.

`dotnet test` needs the `global.json` opt-in already in the repo: xunit.v3 runs on
Microsoft.Testing.Platform, and the .NET 10 SDK no longer bridges that through VSTest.
Individual test projects are also executables, so running one directly works too -- and that is
how you run just the fast half:

```
./tests/RoseMcp.UnitTests/bin/Debug/net10.0/RoseMcp.UnitTests.exe
./tests/RoseMcp.IntegrationTests/bin/Debug/net10.0/RoseMcp.IntegrationTests.exe -class '*RenameTests'
```

Run a worker standalone against a fixture -- the fastest way to debug Roslyn behaviour without
the broker in the way:

```
dotnet run --project src/RoseMcp.Worker -- --solution tests/fixtures/WithGenerator/WithGenerator.sln
```

A solution whose configurations are not `Debug`/`Release` needs to be told which one, and anything
whose target framework is chosen by some other property needs that property:

```
dotnet run --project src/RoseMcp.Worker -- --solution D:/repo/A.slnx --configuration Debug-2027
dotnet run --project src/RoseMcp.Worker -- --solution D:/repo/A.slnx -c Release -p RevitVersion=2027
```

## Getting it actually used

An agent's default reflex for C# is grep plus manual editing, and a semantic tool only wins if
reaching for it is cheaper than that reflex. Three things carry that, in descending order of effect:

1. **Server instructions** (`ServiceCollectionExtensions.Instructions`). Sent during `initialize`,
   so the model reads them before choosing an approach -- unlike tool descriptions, which are only
   read once a tool is already under consideration. Keep them short and framed around what goes
   wrong with the default approach, not around what the tools do.
2. **No setup call.** Every tool finds its own solution, from a supplied path or from the working
   directory. A tool that needs `workspace_open` first loses to grep before it is ever tried.
3. **Registration in the consuming repo.** Add to that project's `CLAUDE.md`:

   ```markdown
   ## C# navigation and refactoring

   Use the Roslyn-backed `rose_*` MCP tools rather than grep or find-and-replace for C# in
   this repo: `rose_find_references` for usages, `rose_rename_symbol` for renames,
   `rose_diagnostics` to check code compiles. To change code in a file that already exists,
   `rose_replace_member`, `rose_replace_body` and `rose_add_member` address a member by name,
   refuse code that does not parse, format what they write, and report what the edit broke --
   so there is no build in the edit loop. `rose_change_signature` adds, removes or retypes a
   parameter across every override, implementation and call site at once. Pass `usings` on any
   of those when the code needs an import, or `rose_add_using` for code written another way; where
   you cannot say which namespace a name needs, `rose_resolve_name` searches for it and refuses to
   guess between two. Before running anything out of `bin`, `rose_build_freshness` says whether it
   is this code. Source-generated code is only readable via `rose_list_generated_documents` /
   `rose_read_generated_document`.
   ```

Register the server with:

```
claude mcp add rose -- <path>/RoseMcp.Server.exe
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

### What this method has already turned up

An agent session that added the whole live-app debugging and XAML surface to this repository -- ten
commits, 109 unit and 144 integration tests -- used **zero `rose_*` tools on this repository's own
code.** Every read was `grep`; every check was `dotnet build`, used as a syntax checker dozens of
times at 14--25 seconds a go, while `rose_diagnostics` sat unused.

Roughly fourteen distinct failures in that session were mechanical rather than logical: not one was a
wrong decision about what to change, all of them were in *applying* a change already decided.
Stripped CRLF failing `IDE0055`. A heredoc eating a backslash so a native provider wrote to a
tab-named path. A splice that dropped a `private:` and duplicated a `}`. A call site missed and found
only from `CS7036`. Every one is a category Roslyn cannot produce.

The diagnosis is the part worth keeping: **a non-semantic edit path poisons the read path.** Because
the editing was textual, the workspace was permanently mid-edit and a build was being paid for
anyway -- so the semantic reads were never worth reaching for. That is why "let an agent write C#
semantically" (issue #30) ranks above every other feature: it is not one more tool, it is what pulls
the existing surface into use.

None of that was discoverable by reasoning about the tool surface. It came from using it, badly, at
length, and then counting.

## Conventions

Enforced by `.editorconfig` where the analyzer can express them, by review where it cannot.

- **Tabs**, not spaces.
- **File-scoped namespaces**, matching the folder they live in (IDE0130). A directory rename is
  otherwise invisible to the compiler.
- **Braces on their own line** -- Allman, everywhere.
- **Conditionals get braces**, with one exception: a simple control-flow body kept on the same
  line may go unbraced.

  ```csharp
  if (document is null) return null;      // fine -- return/continue/break/throw
  if (!TryResolve(path, out var project))
  {
      return WorkspaceResult.NotFound(path);   // anything else gets braces
  }
  ```

  `.editorconfig` can only express `csharp_prefer_braces = when_multiline`, which is close but
  not exact. The rule above is the intent.
- **Readable `if` statements.** Prefer an early-return guard over nesting; hoist a compound
  condition into a named local `bool` rather than packing three clauses into the `if`.

  ```csharp
  var isStructuralChange = projectFileChanged || solutionFileChanged;
  if (isStructuralChange)
  ```
- `nullable enable`, warnings as errors, latest language version.

Commit at every milestone boundary and whenever a self-contained piece works. Run `dotnet format`
first so formatting never shows up as diff noise.
