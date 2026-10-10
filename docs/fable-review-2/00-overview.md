# Overview: the answers, and the work

Synthesis of eight reviews of RoseMCP, 2026-09-16/17, at 446 commits. Read this file for the
answers and the card list; read the numbered files for the evidence. Every finding cited here has
a `path:line` reference in its own file.

**Scale.** 155 findings across eight reports: 27 High, 81 Medium, 47 Low. Roughly 6,000 lines of
review over roughly 60,000 lines of production code and 31,000 of tests, in 18 projects.

| File | Findings | H/M/L | Grade |
|---|---|---|---|
| 01 Broker and Server | 21 | 2/10/9 | Adequate; strong core, fragile lifetime and seams |
| 02 Worker and Roslyn | 23 | 5/11/7 | Core strong, edges adequate, **editing stack fragile** |
| 03 LiveApp, debugger, tap | 22 | 2/12/8 | Adequate leaning strong |
| 04 Agentic citizenship | 23 | 7/14/2 | Adequate, and unusually self-aware about it |
| 05 UI code, tests, process | 27 | 3/14/10 | **Strong**, one growing debt; its structural hole closed (#384) |
| 06 IPC and protocols | 10 | 1/3/6 | Adequate tending strong; 11 of 12 boundaries right |
| 07 Hot-reload readiness | 12 | 4/7/1 | Fragile but well-aimed; 5-6.5 weeks to v1 |
| 08 UI usability | 17 | 3/10/4 | Adequate, and **aimed at the wrong job** |

**Closed so far (2026-09-28).**

| Card | Findings | Shipped in |
|---|---|---|
| 0a | UIP-14, UIP-25 (part) | #295 |
| 0b | AGT-01 (measured), AGT-21 (measured) | #295 |
| 0c | BRK-12, UIP-17 (part) | #295 |
| 0d | IPC-02, BRK-05 | #295 |
| 0e | UIP-23 | #295 |
| 0f | USE inversion 1 | #295 |
| 1 | BRK-01, AGT-10, BRK-20 (the hop) | #305 |
| 1b | BRK-20 (the pin) | #326 |
| 6 | WRK-06 | #306 |
| 2 | LIV-03 | #270 |
| 3 | LIV-02 | #265 |
| 4 | UIP-15, LIV-07 (the honest result) | #317 |
| 5 | IPC-01, LIV-07 (the id), LIV-08, IPC-03 | #323 |
| 7 | WRK-08 | #269 |
| 9 | WRK-01 | #275, #276, #278 |
| 9b | WRK-23 | #418 |
| 10 | WRK-04, WRK-05, WRK-14, AGT-03 | #418 |
| 8 | AGT-17 (the overreach half) | #333 |
| 8 | WRK-03, WRK-19 | #361 |
| 8 | WRK-15 | #424 |
| 8 | AGT-17 (the `rose_format` half) | #427 |
| 1c, 1d | filed after the review | #366 |
| — | LIV-01 | #265, #268, #274, #281 |

**Tier 0 is done in full** (#295), so everything after it is guarded and measurable. With it, all
seven of tier 1's original wrong-answer cards, two of tier 2's three refactors and fixes for every
issue of the third, and twelve of the 27 High findings -- including **the debugger core and the write
pipeline**, which were the two concentrations of duplication the review named, and the only wrong side
effect in the corpus.

**Tier 1 is done in full**, including the two wrong answers filed after the review (#366). **Card 8's
issues are fixed**, AGT-17 with them (#427). All are closed but #360, whose fix is on the tier
branch and which closes with it. What is left of it is the structural refactor WRK-02
argues for, card **8b**, which nothing known is broken without, so whether to do it or decline it is
still to decide.

Issues filed after the review, up to #362, are triaged into the cards below. The ones no card fits
are listed after tier 6, so none of them is re-derived from scratch.

Three cards came out of closing others: **21a**, **11f**, and card 0e's finding
that three of the phrases the comment convention lists are not history clauses at all. Card 9 also
found a wrong answer the review missed -- four write tools reporting a project clean while the
caller's errors sat in it.

Each closed finding is struck in its own file: the pull request, the problem, the state.

---

## The headline

**RoseMCP is not vibe-coded. It is carefully built and under-mechanised.**

That phrase is from the test and process review and it survives contact with all eight. The parts
that are hard to get right have had real thought spent on them and the reasoning is written down:
the freshness barrier, the detach protocol, the routing order, the tap's tier split, the event
buffer, the phase-and-slot test scheduler. Four separate reviewers independently described a core as
"strong" and its surrounding layer as the problem. No reviewer found a wrong *semantic* answer from
the Roslyn half in a live exercise.

**Amended:** #299 found one days later -- a body-only edit left every later diagnostics read served
from cache -- so read that as "the review did not find one" rather than "there are none".

What is missing is not care. It is **binding**: the rules that care produced live in prose, in
conventions, and in data, rather than in types, registrations and tests. That single sentence
explains most of the 150 findings, and the card list below is organised around it.

---

## The eight questions, answered

### 1. How maintainable is the code?

**Good in the middle, thin at both ends.** Naming is excellent and the comments carry genuine
reasoning in the "X, because Y" shape the conventions ask for. Three reviewers said explicitly that a
refactor which loses the comments loses the design.

Against that: three copies of `ToolErrorReporting` (BRK-06, AGT-19), two classes that are the same class twice (UIP-09),
and a comment debt of 43
history clauses and 153 issue tags, 47 of the 49 issues named being closed (UIP-23). That debt was
reported as growing and is now merely large: a per-file baseline means it can only shrink, and the
count it grew past was measured with three phrases that turn out not to indicate history at all.

The concentration was diagnostic: the C# editing stack and the debugger core carried most of the
duplication, and both were the newest code. Both have since been consolidated — `EditPipeline` for the
one, and `CorDebugSession` cut from 2,396 lines to 879 for the other — which is the review's main
prediction holding up.

### 2. How well is it architected?

**Strongly, at the level of process and layer; weakly at the level of enforcement.**

The big calls are right and would be hard to improve: one warm worker per solution as a separate
process, a broker with no Roslyn reference, `Ui.Core` as plain `net10.0` so the UI logic is testable,
`Contracts` with no package references so every host can take it, the tray hosting the broker
in-process so its window reads live state. The reasons are recorded and the reasons hold up.

Inside those layers, responsibility leaks in predictable places. Session lifecycle logic sits in the
tool layer (BRK-09), visual-tree paging sits in the broker against its own decision record (BRK-11),
and `XamlDiff` hard-codes UWP type names in a framework-neutral library (LIV-09). `CorDebugSession`
owning six unrelated concerns (LIV-01) was the fourth, and is now five types.

### 3. Which patterns can be inverted into a pit of success?

This is the question with the best answer, because **the repository has already solved it four times
and does not seem to have noticed the pattern.**

`ToolSurfaceTests`, `SecurityModelTests`, `ToolBudgetTests` and `ToolParityTests` each turn a rule
into a mechanism that cannot be forgotten. `WithCallOrigin` / `WithToolErrorMessages` as request
filters do the same for cross-cutting facts. `WorkspaceSession` makes the freshness barrier the only
door to a `Solution`. `DiskTrackerUpdate` is opaque so the tracking table cannot be committed early.

And then the test review found the boundary exactly:

> Every rule with a named type in `Contracts` has a structural guard. Every rule that is a property
> of an *arrangement* has none.

So: "every result carries a revision" was guarded on 3 tools out of ~45 and review-only for the rest;
it is now enumerated over the declared surface, and its other half is a compile-time constraint
(card 0c). The comment conventions had no CI grep and now have one against a baseline (card 0e).
The published layout is one file every party reads or is tested against (card 21a), and the stdout
rule and the tap's tier rule are each checked over the source rather than held by review (card 21).

**About fifty inversions are proposed across the eight files**; the ones that had to come first were
Tier 0, and are built (PR #295). The seven highest-leverage:

1. ~~Six copied write conventions replaced by one pipeline (WRK-01).~~ **#275, #276, #278.**
2. ~~One compilation-backed symbol resolver, so an address that resolves for one tool resolves for
   all (WRK-04, AGT-03).~~ **#418.**
3. ~~A path type that cannot be resolved without a base (BRK-01, AGT-10).~~ **#305.**
4. ~~Nine fields and five spellings of "is the target stopped" replaced by one state (LIV-02).~~ **#265.**
5. ~~One framed-message type for every pipe (IPC-01, LIV-07, LIV-08).~~ **#323**, for the tap's pipe
   alone: it is the only one carrying a caller's text.
6. ~~Attribution by runtime type check, replaced by one the compiler enforces (BRK-12).~~ **#295.**
7. ~~A fact computed for a window that no window names, caught by a test (USE-01, USE-03).~~ **#295.**

### 4. Are we a good citizen in agentic flows?

**Yes on intent and prose, not yet on cost.** The descriptions are the best tool-description writing
the reviewer had read in an MCP server, and each names the alternative it beats. The thesis is stated
in the source: an agent reaching for grep is not choosing badly between known options, it is not
aware there was a choice.

The implementation undercuts it. Ranked reasons tools lose to grep, from the issue corpus plus five
reviewers' live experience:

| Rank | Reason | Note |
|---|---|---|
| 1 | The answer is too big to use | The commonest loss, and the only one where the tool *worked* |
| 2 | The name the caller wrote cannot be addressed | The addressing grammar is the central claim and it is not total |
| 3 | The error does not say what to do | Two failed round trips and the agent goes back to what it trusts |
| 4 | The write is not trusted | Once you re-read the file to check a write, `Edit` is strictly cheaper |
| 5 | The edit cannot be expressed | Extract-method; changing a comment |
| 6 | Habit, with no defect behind it | Ranking sixth means the descriptions are working |

**What is absent from that list is the good news: precision.** No reviewer this round found a wrong
semantic answer. Every loss is cost, reach or explanation. The backlog is a size-and-errors backlog,
not a correctness one.

The measured case, reproduced twice independently including by the orchestrating session:
`rose_outline` on a 441-line class, with documentation *and* signatures explicitly turned off, cost
roughly 13x what one grep costs and returned strictly less (no signatures). The absolute solution
path repeats once per member. `includeSignatures=false` does not remove the declaration text, because
`preview` still carries the source line. On a WinUI code-behind it is worse: the outline merges in
every member of the markup-generated partial, none marked as generated, and passing a file path does
not filter them out.

`rose_outline` is the tool the server instructions name for "what a type contains" and describe as
the read that comes before most edits. It is the single highest-leverage fix on this surface.

### 5. What could we do better?

Six things, in order of leverage. All are expanded as cards below.

1. Draw the text/syntax line once in the writing stack, and six open fidelity issues close as a
   consequence rather than as six fixes.
2. Put the live-app third of the product into CI. It is the newest, least conventional and most
   bug-dense code and it has no continuous coverage at all.
3. Make results small enough to use.
4. Give the rules that are arrangements the same mechanism treatment the rules that are types already
   have.
5. Re-aim the UIs at the supervising user (question 8's territory).
6. Fix the analyzer loader, which is why Rose calls this repository degraded.

### 6. Are the technologies and protocols for IPC appropriate?

**Yes: eleven of twelve boundaries were right, and the twelfth, the XAML tap pipe, was a payload
problem rather than a transport one, fixed by #323.**

MCP over stdio for the internal parent-child hops is called the standout decision: not the reflex
choice, and it pays three times over, because the worker and the live-app host are *also* standalone
MCP servers a person can drive with any client, the tests exploit exactly that, and progress and
cancellation arrive on a protocol the broker already speaks outward.

The costs are real and mostly unpriced: JSON with no binary path, cancellation re-implemented by hand because the SDK never sends it, and one SDK's quirks
load-bearing in four processes.

Polling for the inspector is **correct**, and the premise that it might not be was wrong: the event
tail is already a long poll with a thirty-second wait, so its latency equals SSE, and interval
polling is used only for state panes where a re-read is idempotent.

### 7. How far is C# hot reload?

**5 to 6.5 focused weeks for a credible v1** (plain .NET plus unpackaged WinUI 3, debugger path); 7
to 10 for both architectures and the framework matrix. The XAML live-edit epic took about a week,
and the difference is that it consumed a framework-provided apply API where this one must build its
own.

Of 28 capabilities audited: 9 present, 8 partial, 11 absent, and the absences cluster in exactly two
places — delta computation and the apply channel. Everything else (a warm immutable solution, a
freshness check that *is* the baseline precondition, a debugger attached from birth, a stop/resume
machine, a PDB reader, and a proven apply *shape* in the XAML live edit) already exists.

Three verified facts shape the plan:

- **Attach can never hot reload.** The JIT flag for Edit and Continue may only be set inside the
  module-load callback, so an already-loaded module can never be armed. Launch is eligible; attach is
  permanently not, and nothing in the product says so.
- **The Roslyn hot-reload service is internal and fenced.** Its `InternalsVisibleTo` grants name three
  Microsoft strong-named assemblies; no assembly RoseMCP can build satisfies one. The route is
  reflection behind a sealed adapter that binds every member at session start, which is what
  Microsoft's own generator does.
- **One spike decides the shape.** Whether Features 5.9.0 still carries a reachable entry point could
  not be verified. That is milestone one, two to three days, and a negative answer adds two to three
  weeks.

Recommendation: build the debugger path first because it is the smaller delta against what exists and
proves the pipeline end to end; build the managed-agent path second because it is what makes it a
product, since the common loop is run-the-app, edit, see it with no debugger attached, and only that
path triggers the framework handlers that make a WinUI app re-render. The emit half is shared, so
first is not throwaway.

### 8. Has enough design care gone in for the growth to hold?

**Yes for the structure, no for the aim.**

The structural answer is question 1 and 2's: the process split, the layer boundaries and the
invariant discipline all scaled from "Roslyn workspace MCP" to a debugger and a tap without breaking.
The newest third carries more debt than the oldest, which is expected, and has less mechanism around
it, which is the risk.

The aim is the finding you should take most seriously, and it is the one you suspected:

> All three surfaces are built for a human *doing* the work, and RoseMCP's user is a human
> *supervising an agent* doing it.

That misaim explains the inspector having four panes that are smaller Visual Studio panes and none
showing what the agent is doing; the tray's activity history, the only record anywhere of what an
agent did to your solution, being eight entries in a collapsed expander dropped on close.

The sharpest single fact in the whole review: **the broker computes a dozen facts specifically so a
window can show them, argues in each XML summary why a reader needs them, and no window renders
them.** The degraded reasons, the analyzer load failures, the restore state, the per-project health,
the age of the information, the install location, the session notice. Cheapest wins in the
repository, all in one place.

---

## Ranked card list

Ordered by value per unit of effort. Each card names its findings; each finding has evidence in its
file. Existing issues are named so nothing is filed twice.

**Dependencies all point downwards, which is why tier order is also work order.** Every gate found
so far runs from a later tier to an earlier one, never the reverse, so working the tiers in order
satisfies them without anyone tracking a graph. One is left:

| Card | Waits on | Why |
|---|---|---|
| Tier 6 | 3, and ideally 8 | An apply needs an explicit target state, and the emit sits on the write pipeline |

### How the pit-of-success inversions relate to the cards

Roughly fifty inversions are proposed across the eight files. They are not a separate workstream,
and most need no separate card, because they relate to the cards in one of two ways:

- **The card *is* the inversion.** Cards 1, 3, 9 and 10 are the four biggest inversions written as
  work: a path type that cannot be resolved without a base, a target-execution union, one write
  pipeline, one symbol resolver; all four are done. Doing the card badly and doing the inversion are
  the same alternative, so there is nothing extra to schedule -- only a note in the card that the
  mechanism, not the fix, is the deliverable.
- **The inversion is a guard that must land *before* its cards**, because the cards are exactly the
  work it protects. These are cheap, ungated and few. They were Tier 0, and are done (PR #295).

### ~~The pattern four reviewers found separately: computed, returned, never consumed~~

**#295.** Four subsystems computed a fact whose summary argued a reader needed it, and nothing read
it. A test fails on a fact nothing consumes unless it is listed with the reason.

### Tier 1 — wrong answers and wrong side effects

These produce confident wrong results today. Everything else is cost.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| ~~1~~ | **#305.** A relative path was measured from the directory the broker process started in, so a call from one worktree could edit the same-named file in another and report success. It is measured from the calling session's directory, and the hop on from there is absolute-only. | BRK-01, AGT-10, BRK-20 | — | — |
| ~~1b~~ | **#326.** A warm worker stood in its solution's directory, which Windows holds open against deletion, so an opened worktree could not be removed until the broker went. Workers stand in an empty folder of Rose's own. | BRK-20 | — | — |
| ~~2~~ | **#270.** A breakpoint hit was attributed by method token alone, so two bindings in one method could not be told apart. Hits are matched on the instruction offset. | LIV-03 | — | — |
| ~~3~~ | **#265.** A dead target reported as stopped. Execution is one state with one spelling. | LIV-02 | — | — |
| ~~4~~ | **#317.** A XAML request the host had timed out on could still run in the app, and the caller was told only that it failed. A timed-out verb that changes the app says the change may still land; cancelling one in flight is declined. | UIP-15, LIV-07 | — | — |
| ~~5~~ | **#323.** The tap's request side did not escape what its reply side unescaped, so an edit with a tab or a newline in its value landed and then reported that it had not. Host and provider share one wire contract, with a request id, a versioned greeting and a per-session key. | IPC-01, LIV-07, LIV-08, IPC-03 | — | — |
| ~~6~~ | **#306.** One compilation was asked about another's symbol, so resolving a name and every write that worked out its own imports failed in most of this repository, naming an argument the caller never sent. A symbol is mapped into the asking compilation before it is asked about. | WRK-06 | — | — |
| ~~7~~ | **#269.** Every analyzer was flattened into one load context, so two versions of one analyzer could not coexist. They are isolated per directory. | WRK-08 | — | — |
| ~~1c~~ | **#366.** A move could leave the member declared in both types and report success, because it looked the declaration up where it stood before the call sites were rewritten. Both ends of a move are found by what they were marked with, and one that cannot be found is an error. | — | — | — |
| ~~1d~~ | **#366.** Diagnostics seemed stale for a file new to the project. It was a body-only edit served from cache, which #299 had already fixed. | — | — | — |

### Tier 2 — the three structural refactors

Each closes a class of bug rather than a bug, and each is a prerequisite for something else.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| ~~8~~ | **#333, #361, #417, #418, #424, #427.** Writes damaged layout and comments nobody asked them to change, each in its own way, and `rose_format` called the result formatted. Each damaged path is fixed where it arose, a write names every line it changed outside what it was asked, and `rose_format` says what it checked. | AGT-17 | — | — |
| 8b | **Draw the text/syntax line once in the writing stack.** Card 8's issues are fixed locally to each path, and those paths still handle source as text before the formatter sees it: a body edit splices strings, a signature change takes the caller's separators when the caller wraps its list, a doc comment is told from prose by its first character, and four string re-indenters in `MemberSyntax` reconcile the caller's indentation with the file's. Syntax in, syntax out; text only inside the whitespace pass; one trivia pass after the formatter replacing the re-indenters. Nothing known is broken without it, so it closes a class rather than a bug, and **whether to do it or decline it is undecided**. A write names the lines it changed outside what it was asked, and `rose_format` names a wrapped list whose items begin at different depths; damage of any other shape inside a span the caller asked for is seen by neither. | WRK-02 | — | M-L |
| ~~9~~ | **#275, #276, #278.** Six services each carried their own copy of the write conventions, and four tools on them reported a project clean while the caller's errors sat in it. One pipeline owns the conventions. | WRK-01 | — | — |
| ~~9b~~ | **#418.** Most tools wrote out their own wait for the workspace beside two helpers that did it, so a new tool could copy one that let a cold load go unreported. Every tool reaches the workspace through one helper, and a test refuses a tool given the means to go round it. | WRK-23 | — | — |
| ~~10~~ | **#418.** Two and a half resolvers disagreed about what a name meant, so a positional record property, a type named for its namespace and a library member sharing a source name's last segment were each unreachable from some tool; the import search answered a call with a type and a namespace-qualified name with its first segment. One resolver asks the compilation for every tool, and the import search asks it how the name is used. | — | — | — |

### Tier 3 — make Rose win against grep

Highest leverage on adoption. Cheap relative to impact.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| ~~11~~ | **#374.** The reads answered with a declaration record per outlined member, raw or whole documentation, an absolute path on every file and a code-behind's generated half, one symbol per call, and a first-few sample past a cap. They answer cheaply by default and take a list of symbols, documentation is prose, a path is relative to the caller, a file outline lists that file, and every capped list says what it left out with a filter for each group. | AGT-01, AGT-02, AGT-06, AGT-11, UIP dogfooding | — | — |
| ~~11b~~ | **#375.** A write result was mostly the caller's own code read back, the same absolute path several times, and notices that fired on every call. A write names each changed file once, relative to the caller's directory where it lies under it, with where it changed; the diff is opt-in, and a notice says only what is true of the call and no field already says. | AGT-21 | — | — |
| 11i | **`rose_diagnostics` entries.** Every entry still carries a `helpLink` no agent opens and an absolute path. A write's diagnostics already leave the link off and name the path relative to the caller (`WritePaths`, from `CallerPaths.KnownOrigin`), so this read can take the same step in the same place. | AGT-21 | — | S |
| ~~11c~~ | **#376.** Every result named its workspace by a short key that no argument accepted. Every tool that takes `workspace` accepts the key too, but the one that starts a load. | AGT-21 | — | — |
| ~~11d~~ | **#377.** The four debug bookkeeping tools took one location or id each, so instrumenting a path cost a model turn per method. Each takes a list and answers every entry with its own status, and one bad entry never fails the rest. | AGT-22 | — | — |
| ~~11e~~ | **#378.** An answer past its cap was the first few references and a truncation flag, and three of the four facets on every reference could not be asked about. Past its cap the answer is the shape of the references, every facet is a filter, and a filter that keeps nothing says so. | AGT-23, AGT-05 | — | — |
| ~~11f~~ | **#157, #379.** A worker outlived the worktree it was opened on and an idle one held its memory for the life of the broker, a session whose debug host had died was polled every second for as long, and no session could see what was warm. A long-lived broker stops idle and orphaned workers, a dead host's session is shown ended and then dropped, each says why where a person looks, and a session can list what is loaded by the key that names it. | BRK-04 | — | — |
| 11g | **A path that does not exist yet is passed over as a routing hint**, so `rose_add_file` into another checkout is answered by the calling session's workspace every time, although the routing invariant says an absolute path is honoured wherever it points. It fails safely, and the refusal is the defect: it says the path is inside no project, which is false, and suggests an argument that would not help rather than `solution`, which would. Route a path that names nothing by its nearest existing ancestor, the way the tool will place the file. | new | #357 | S |
| ~~11h~~ | **#358.** A typed tool's text was escaped by the SDK's default encoder, `+` and `<` included, except on a call carrying an undeclared argument, whose text was rewritten readably. Every host writes tool text with one relaxed encoder. | new | — | — |
| ~~12~~ | **#249.** An argument sent under a name the tool does not declare was dropped in silence, and the refusal then reported the value as missing. A refusal names the argument and the declared name it most likely meant, and a call that succeeds without it says so in its notices. AGT-08's other suggestion, one word for imports everywhere, is undecided. | AGT-08 | — | — |
| ~~12b~~ | **#380.** Three debug tools answered with a sentence naming no session, and the guard against that exempted the whole live-app surface. Every tool answers with a record, and the live-app exemption covers workspace attribution only. | BRK-21 | — | — |
| ~~13~~ | **#431.** Errors named CLR parameters the caller never sent, a framework's exception read like a refusal, and an unbuilt reference's load diagnostic gave no remedy. No refusal carries a parameter name, a leaked exception says whose failure it is, and status names the project to build. | AGT-04, WRK-07 | — | — |
| ~~14~~ | **#382.** A read from a degraded workspace said nothing of it, so a clean answer from a broken one read as a clean bill of health, and a tool path that had died left status calling the workspace healthy. Every read says in one line that its workspace is degraded and why, and a dead tool path degrades it. | AGT-12 | — | — |
| ~~15~~ | **#383.** `rose_find_implementations` answered a framework interface with every dependency's implementations and could not be narrowed, and `rose_find_references` listed one declaration several times. The first lists this solution's source only and takes `project`; each declaration and each use is listed once. | IPC dogfooding, USE dogfooding, AGT-07 | — | — |

### Tier 4 — mechanism: make the rules structural

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| ~~16~~ | **#384.** The tap, the visual tree, the overlay and the live-edit apply ran in no CI job, so three invariant documents were held by review alone. A job sets the runner up for the probe apps and runs their tests, and fails rather than skips where anything is missing. | UIP-25 | — | — |
| 17 | *(moved to card 0a -- the accidental inclusion turned out to be proof that debugger tests run fine on a hosted runner, so it is widened deliberately rather than reverted.)* | UIP-14 | new | -- |
| 18 | **Share fixtures on the Roslyn half: the tests that write.** A test that only reads shares one load per fixture (#39). The tests that write still copy and load a fixture each, which is most of the loads left. What remains is a copy that keeps its restore output for all but the generator tests, then a pool of loaded workspaces handed out the way the live-app slots are, with a hand-back check. The broker-process classes (`BrokerForwardingTests`, `IdleEvictionTests`, `WorkspaceRoutingTests`) start real workers rather than load through a test session, and are their own question. | UIP-13 | #39 | L |
| 19 | *(moved to card 0d -- it is worth having before the work starts, not after.)* | IPC-02, BRK-05 | new | -- |
| ~~20~~ | **#385.** A call could not be traced across the processes it crossed, and nothing said which file was a workspace's worker log. Every log line carries the id of the call it was written for, the same in every process, and a workspace names its worker's log. | BRK-15, IPC-07 | — | — |
| ~~21~~ | **#386.** The stdout rule, whose violation reads as a protocol error far from its cause, and the tap's header tiers were each held by review alone. Both are checked over the source by the unit suite, on every runner; formatting and linting the C++ and the PowerShell is declined. | UIP-18, UIP-24 | — | — |
| ~~21a~~ | **#387.** Five parties each wrote the published layout down for themselves, two of them packaged content a user runs, so a layout change passed every test and failed at install time. They all read one committed layout, or are tested against it. | UIP-22 | — | — |

### Tier 5 — the windows, redesigned

Decided 2026-10-11: the windows serve the person supervising agents, and the person debugging by hand.
The design is [`09-windows-redesign.html`](09-windows-redesign.html); open it in a browser. It covers
the use cases the windows answer, mock-ups of the tray, the RoseMCP window, the Inspector and the
panel, and what Rose needs underneath. The tier lands as one native stack of three pull requests,
worked back to back: 5a into `main`, 5b on 5a, 5c on 5b. It supersedes the first cut of this tier,
cards 22 to 26 (#388 to #392).

| # | Card | Use cases | Issues | Effort |
|---|---|---|---|---|
| **5a** | *Foundations: data and protocol, with today's windows still in place.* | | | |
| 5.1 | **Notice a rebuilt analyzer, generator or code fix.** Every answer keeps using the old build after one is rebuilt, and nothing says so. | B5 | #455 | S |
| 5.2 | **Build identity.** Every process carries its commit, a dirty flag, its build time and its checkout. Handshakes compare commits. | A2, A8 | #456 | S |
| 5.3 | **A relay that outlives a deploy.** It reconnects by itself, and sends `tools/list_changed` only when the tool hash differs, so a deploy needs no `/mcp`. | A9, A2 | #457 | M |
| 5.4 | **Who's connected.** The relay names the session it serves, so the broker knows its clients. | A3, B4 | #458 | S |
| 5.5 | **The activity record.** Every call, attributed, with its arguments, response and an edit's diff, kept for 7 days. | B3, B4, C3 | #459 | M |
| 5.6 | **Workspace health with a shape.** Reasons a person can accept, choosing what agents read about them. Agents can acknowledge them. | B1 | #460 | M |
| 5.7 | **The operator API the new windows read.** | — | #461 | M |
| **5b** | *The RoseMCP app: the tray loses its window, and the new one arrives.* | | | |
| 5.8 | **The tray process keeps only its icon.** Three states, one menu, and notifications only for what a person can act on. | A1, A6, A8 | #462 | S |
| 5.9 | **The RoseMCP window, organised by agent.** Agents, Activity, Workspaces and Debugging views, with a memory bar. | A3–A7, B1–B4 | #463 | L |
| 5.10 | **Notes to an agent.** Delivered with its next call. `rose_reply` lets the agent answer. A channel push is a stretch goal. | C8, D8 | #464 | M |
| **5c** | *The Inspector and the panel.* | | | |
| 5.11 | **Debug an app without an agent.** Attach and launch by hand, and share the session with an agent. | E5, E6 | #465 | M |
| 5.12 | **The Inspector's new layout.** The app, the agent's calls and the person's notes in one view. | C1–C6, E1–E4 | #466 | L |
| 5.13 | **The XAML view and the panel.** The agent's edits are marked, and the panel can send a pick, a measurement or a colour to the agent. | D1–D8 | #467, #226 | M |
| 5.14 | **Write down why the magnifier exists.** The OS magnifier filters bilinearly, so it can neither read an exact colour nor show a one-pixel gap. | USE-08 | #393 | S |
### Tier 6 — hot reload

Run as its own epic, after cards 3 and (ideally) the write-pipeline work. Full milestone table with
proofs is in `07-hot-reload-readiness.md`.

| # | Card | Effort |
|---|---|---|
| 28 | **Spike: can the EnC analysis API be reached at all?** Decides everything downstream. | S (2-3 days) |
| 29 | Controlled environment at launch, plus an armed module registry (the module handle is dropped today at the only place EnC can be armed). | M |
| 30 | Worker: pin a baseline, emit a delta. | L |
| 31 | Contracts and transport for delta bytes; pair a workspace with a live-app session. | M |
| 32 | **Apply through the debugger — the probe milestone.** Edit a method while it loops, observe the new value, no relaunch. Carries HOT-06's remaining half: an `Applying(ApplyRecord)` arm on `TargetExecution`, so the safety timer, a detach and a second apply each have to say what they mean during one. | M |
| 33 | Symbols that model the process rather than disk. | M |
| 34 | The managed agent path, which is what makes it a product. | L |

### Filed since the review, and in no card

Open issues from after the review that no card above covers. Each wants a decision: a card of its
own, a place in one, or a line saying why it is declined.

| Area | Issues |
|---|---|
| The integration suite | A probe that will not build fails one test and skips the rest, against its fixture's own rule (#256). The suite lock is per user, so two users do not exclude each other (#280). A running tray hangs the suite and the lock does not check for one (#288). A test asserts on a log line and fails intermittently (#292). Three subsystems fail only under full-suite load (#307). |
| Process | `Check-Comments.ps1` walks the filesystem, so it fails on generated C++/WinRT headers that are gitignored (#304). |
| Live app | `Debugger.Break()` in the target raises no session event, and a native crash says only that the process exited (#273). A XAML tree read ships the whole tree across the pipe whatever was asked for; the paging is applied after it, beside BRK-11's (#322). |
| Tool surface | No tool changes a member's modifiers, AGT-18's "the edit cannot be expressed" again (#330). The surface has outgrown its character budget and its instructions, and a split into three surfaces served by one broker is proposed (#339). Nothing prompts the switch to Rose when a session that began in markdown becomes a C# change (#318). |
| Enhancements | `rose_find_split_options` omits the helpers a moved island still calls, which reorders its ranking (#294). A measured cohesion backlog over `src` (#286). |

---

## What must survive a refactor

A card list that names only problems loses the things worth protecting. From the eight strengths
sections, the shortlist:

- **`WorkspaceSession` as the only door to a `Solution`**, and the two-phase commit of the disk
  tracking table. Called the hardest thing in the worker to get right, and right.
- **`WorkspaceFor` as one public, testable ordering**, with `WorkspaceHints` replacing seventeen
  hand-written fallback chains.
- **The four surface tests** — tool list, security model, budget, parity. The model for every
  inversion above.
- **The request-filter pattern** for origin, session and error conversion. Already the pit of success.
- **The detach protocol**, and timers that know which `StopRecord` they were armed for. Two facts in it
  each cost a target.
- **The cut of `CorDebugSession` into six types**, `CorDebugInspector` first and the other five after it.
  The seam reasoning is in each class summary, including what each deliberately does not own.
- **The tap's tier split enforced by include order**, so a projection type reaching into the shared
  layer fails to compile.
- **`RoseMcp.Symbols`**, which is exactly what the architecture table claims.
- **`HoldKeeper` and `OperatorClient`**, the two most carefully reasoned classes in the product, and
  `HoldKeeper` is also the one supervision primitive Visual Studio has no analogue for.
- **The phase-and-slot test scheduler**, called genuinely original work.
- **The tool descriptions**, which three reviewers praised independently.
- **MCP over stdio for internal hops**, for the three reasons the IPC review gives.

## Rose defects this review found by using Rose

Filed here so they reach the issue tracker. Several are not in any existing issue.

1. ~~Compact `rose_outline` is not compact, and loses to grep on both size and information (AGT-01).~~ **#374.**
2. ~~`includeSignatures=false` leaves the declaration text in `preview` (AGT-01).~~ **#374.**
3. ~~`rose_outline` on a WinUI code-behind merges the generated partial, marks none of it generated
   despite the description's promise, and ignores `filePath` (05 dogfooding).~~ **#374.**
4. ~~A metadata symbol is unreachable whenever any source symbol shares its leaf name (AGT-03).~~ **#418.**
5. ~~A positional record property cannot be addressed by name from any tool (WRK-04).~~ **#418.**
6. ~~`rose_find_implementations` has no way to ask "in my solution" (IPC dogfooding).~~ **#383.**
7. ~~`rose_find_references` lists one definition three to four times at different columns (USE dogfooding).~~ **#378.**
8. ~~`definitionsOnly=true` reports `truncated: true` over an empty list (AGT-05).~~ **#378.**
9. ~~`rose_resolve_name` without a file path fails with a leaked Roslyn parameter name (AGT-04).~~ **#431.**
10. `rose_build_freshness` counts `obj/` artefacts as sources, so it can name a generated editorconfig
    as the newest source file (HOT-10).
11. ~~`rose_symbol_info` returns `"source":[]` when source was not requested, which reads as "no source".~~ **#374.**
12. No tool reads XAML, though the worker knows a great deal about it.
13. No way to ask Rose what its own tool listing looks like to a client, so surface changes are
    reviewable only as pass/fail.
14. No negative-space or bulk query ("which members of this type does nobody reference"), which sent
    two reviewers to grep. `rose_find_split_options` (#290) answers a question about a type rather
    than a symbol, but not this one.

## Suggested reading order for splitting cards

1. This file.
2. `09-windows-redesign.html`, which settled the strategic question `08-ui-usability.md` raised.
   Tier 5 depends on it and nothing else does.
3. `07-hot-reload-readiness.md` "What exists today" and the milestone table.
4. `02-worker-roslyn.md` finding WRK-02, which is what is left of Tier 2.
5. `05-ui-tests-and-process.md`'s invariant-to-test table. It is the map for Tier 4.
6. The rest as the cards demand.
