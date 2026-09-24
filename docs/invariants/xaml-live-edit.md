# XAML live edit

Read before changing `rose_xaml_*`, `src/RoseMcp.XamlDiff/`, or the apply path in `src/RoseMcp.LiveApp/Xaml/`.

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
  reported success. Removal also has to be *forgotten* from the node list at once, closed over
  descendants, rather than left to the framework's own Remove callback: that callback is what keeps the
  list true over a session, but it arrives when the framework gets to it, and the next edit in the same
  batch resolves against the list now. Forgetting twice costs nothing, since the second finds the
  subtree already gone.
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
- **One XAML request at a time, and every path takes the lock exactly once.** The live-app host
  serves MCP calls concurrently -- measured, not assumed: two tree reads issued together finished in 118ms
  against a warm single read of 112ms -- and every XAML request shares one pipe, which carries one
  request and one reply at a time. The measurement was taken against a channel of files and the
  conclusion outlived it: ten concurrent pairs against the probe produced several fifteen-second waits
  for a snapshot the other call had already consumed, and once a tree of 22 elements where the app has
  24, returned with no detail set. The last is why this is a lock and not a documented limitation,
  since a truncated tree hands out handles for a tree that is not there, and a pipe fails no better --
  two requests interleaved on one stream pair each reply with the wrong question. Serialised rather
  than given a channel each, because the provider does everything on the app's UI thread, so a second
  pipe would buy no parallelism from a single-threaded consumer. Every public entry point takes it
  once and calls a `Core` method that assumes it is held, so no path takes it twice -- which is what
  keeps the choice of lock free rather than load-bearing, since a `Core` method that took the lock
  itself would deadlock under a `SemaphoreSlim` and pass under `System.Threading.Lock`. Do not
  conclude from a passing concurrency test that the lock is unnecessary -- the silent failure
  appeared once in ten, and the test was confirmed to fail with the locks removed.
- **A bound on a provider request bounds the waiting, not the request.** The frame is in the pipe
  before the wait starts, the provider serves every verb on the app's UI thread through
  `RoseTapRunOnUiThread`, and nothing on the host side can cancel work already handed to that
  thread. So a mutating verb the host has given up on can still run, and a later read can observe
  it -- which is "the tool reported failure and did the thing anyway", the class of wrong answer
  this product exists against, and the caller least equipped to notice is an agent. A timed-out
  request therefore says so: `XamlRequestKind` decides from the verb whether the app can still
  change, and `XamlChannelBounds.Unanswered` cannot compose a message without being told which
  request it is about. The list it keeps is of *reads*, so a verb nobody classified is warned about
  rather than silently trusted, and a unit test holds it against the provider's own dispatch.
  <br>
  What is not done, deliberately, is making the request cancellable: the pipe correlates a reply
  with a request by position, so a request id in the frame header and an `abandon` the provider
  checks before dispatching is the fix that would let the host stop one. That is one framed message
  type's worth of work on a hazard nothing has been observed to hit -- no late reply has ever
  reached the stale drain's warning in a kept log -- and it belongs with the wire format's other
  correlation work rather than on its own.
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
