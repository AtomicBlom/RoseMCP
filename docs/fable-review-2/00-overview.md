# Overview: the answers, and the work

Synthesis of eight reviews of RoseMCP, 2026-09-16/17, at 446 commits. Read this file for the
answers and the card list; read the numbered files for the evidence. Every finding cited here has
a `path:line` reference in its own file.

**Scale.** 154 findings across eight reports: 27 High, 81 Medium, 46 Low. Roughly 6,000 lines of
review over roughly 60,000 lines of production code and 31,000 of tests, in 18 projects.

| File | Findings | H/M/L | Grade |
|---|---|---|---|
| 01 Broker and Server | 20 | 2/10/8 | Adequate; strong core, fragile lifetime and seams |
| 02 Worker and Roslyn | 23 | 5/11/7 | Core strong, edges adequate, **editing stack fragile** |
| 03 LiveApp, debugger, tap | 22 | 2/12/8 | Adequate leaning strong |
| 04 Agentic citizenship | 23 | 7/14/2 | Adequate, and unusually self-aware about it |
| 05 UI code, tests, process | 27 | 3/14/10 | **Strong**, one structural hole, one growing debt |
| 06 IPC and protocols | 10 | 1/3/6 | Adequate tending strong; 11 of 12 boundaries right |
| 07 Hot-reload readiness | 12 | 4/7/1 | Fragile but well-aimed; 5-6.5 weeks to v1 |
| 08 UI usability | 17 | 3/10/4 | Adequate, and **aimed at the wrong job** |

**Closed so far (2026-09-20).**

| Card | Findings | Shipped in |
|---|---|---|
| 0a | UIP-14, UIP-25 (part) | #295 |
| 0b | AGT-01 (measured), AGT-21 (measured) | #295 |
| 0c | BRK-12, UIP-17 (part) | #295 |
| 0d | IPC-02, BRK-05 | #295 |
| 0e | UIP-23 | #295 |
| 0f | USE inversion 1 | #295 |
| 2 | LIV-03 | #270 |
| 3 | LIV-02 | #265 |
| 7 | WRK-08 | #269 |
| 9 | WRK-01 | #275, #276, #278 |
| — | LIV-01 | #265, #268, #274, #281 |

**Tier 0 is done in full** (PR #295), so everything after it is guarded and measurable:
`ResultBudgetTests` holds a ceiling for the three shapes tier 3 shrinks, `ProducedFactTests` holds
the 26-entry exemption list that is cards 11c, 11e and 22's worklist, `tools/Check-Comments.ps1`
holds a comment-debt baseline that can only go down, and the debugger suite tier 6 is built on runs
in CI. Also three of tier 1's seven wrong-answer cards, one of tier 2's three refactors, and
five of the 27 High findings — **the debugger core and the write pipeline**, which were the two
concentrations of duplication the review named.

Next at the top: **card 1** (the relative path resolved against the broker) by a wide margin, then
**card 4** (the XAML pipe, and #208 with it), then **card 8** for tier 2's editing work.

Three cards came out of closing others: **9b** (WRK-23 survived card 9, which its own text said it
would not), the layout half of **21** (PR #277 took the parties to the published layout from two to
five), and card 0e's finding that three of the phrases the comment convention lists are not history
clauses at all. Card 9 also found a wrong answer the review missed — four write tools reporting a
project clean while the caller's errors sat in it.

Each closed finding is struck in its own file with what shipped, where the reasoning now lives, and
where the card turned out wrong.

---

## The headline

**RoseMCP is not vibe-coded. It is carefully built and under-mechanised.**

That phrase is from the test and process review and it survives contact with all eight. The parts
that are hard to get right have had real thought spent on them and the reasoning is written down:
the freshness barrier, the detach protocol, the routing order, the tap's tier split, the event
buffer, the phase-and-slot test scheduler. Four separate reviewers independently described a core as
"strong" and its surrounding layer as the problem. No reviewer found a wrong *semantic* answer from
the Roslyn half in a live exercise.

What is missing is not care. It is **binding**: the rules that care produced live in prose, in
conventions, and in data, rather than in types, registrations and tests. That single sentence
explains most of the 150 findings, and the card list below is organised around it.

---

## The eight questions, answered

### 1. How maintainable is the code?

**Good in the middle, thin at both ends.** Naming is excellent and the comments carry genuine
reasoning in the "X, because Y" shape the conventions ask for. Three reviewers said explicitly that a
refactor which loses the comments loses the design.

Against that: five independent answers to "what is this file's indent and line ending" (WRK-03), three
copies of `ToolErrorReporting` (BRK-06, AGT-19), two classes that are the same class twice (UIP-09),
tool-layer boilerplate beside two helpers that already wrap it (WRK-23), and a comment debt of 43
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
What is still an arrangement nobody checks: the stdout rule, the one that corrupts the protocol, has
no guard of its own; the tap's tier rule is prose; and the published layout is asserted against a
layout the test stages itself rather than the one the deploy script writes. Those three are card 21.

**About fifty inversions are proposed across the eight files**; the ones that had to come first were
Tier 0, and are built (PR #295). The seven highest-leverage:

1. ~~One `WriteOperation` pipeline type replacing six copied conventions, with the rewrite as the
   only stage a service supplies (WRK-01).~~ **Built as `EditPipeline`, PRs #275 #276 #278** — with one
   correction worth carrying to the rest: a line that states a **fact** (which compile ran) stays with
   its tool; only a line that **frames** the compile is shared. One voice would have lost the fact.
2. **One compilation-backed symbol resolver** replacing two-and-a-half resolvers, so an address that
   resolves for one tool resolves for all (WRK-04, AGT-03).
3. **A `RepositoryPath.From(raw, origin)` type**, so a relative path cannot be resolved without a
   base and the wrong-worktree write becomes unrepresentable (BRK-01, AGT-10).
4. ~~A `TargetExecution` discriminated union replacing nine fields and five differently-spelled
   guards, so a dead target cannot report as stopped (LIV-02).~~ **Built, PR #265** — the first of the
   seven to land, and the worked example for the rest.
5. **One framed-message type for every pipe**, which deletes the tap's escaping asymmetry and half of
   its correlation problem at once (IPC-01, LIV-07, LIV-08).
6. ~~A generic constraint plus a surface-enumerating test for attribution, replacing a runtime type
   check (BRK-12).~~ **Built, PR #295** — and the constraint did more of the work than the test: an
   unattributable result no longer compiles, which is what "a tool added later cannot forget it" has
   to mean.
7. ~~A test that fails when a UI-facing DTO gains a property no UI project reads, which is the
   structural form of "the broker computes a dozen facts no window shows" (USE-01, USE-03).~~
   **Built as `ProducedFactTests`, PR #295**, generalised to every fact worth computing per item:
   19 properties no window names, and 4 facets no argument selects on.

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

**Yes: eleven of twelve boundaries, and the twelfth is a payload problem, not a transport one.**

MCP over stdio for the internal parent-child hops is called the standout decision: not the reflex
choice, and it pays three times over, because the worker and the live-app host are *also* standalone
MCP servers a person can drive with any client, the tests exploit exactly that, and progress and
cancellation arrive on a protocol the broker already speaks outward.

The costs are real and mostly unpriced: JSON with no binary path, no correlation id on any internal
hop, cancellation re-implemented by hand because the SDK never sends it, and one SDK's quirks
load-bearing in four processes.

Polling for the inspector is **correct**, and the premise that it might not be was wrong: the event
tail is already a long poll with a thirty-second wait, so its latency equals SSE, and interval
polling is used only for state panes where a re-read is idempotent.

The one wrong payload: the XAML tap pipe is the only boundary whose framing was invented rather than
adopted, and the only one whose protocol defects produce wrong answers rather than failures.

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
satisfies them without anyone tracking a graph. The five that exist:

| Card | Waits on | Why |
|---|---|---|
| 11b, path half | 1 | Returning relative paths makes agents send them, so the size win must not land before the resolution fix |
| 11b, notice half | 9 | Eight hand-written notice iterators are why two notices fire unconditionally; one pipeline gives notice discipline a home |
| 11c | 1 | An anchor is only worth accepting once resolution is right |
| 1b | 1 | The working directory can only move once the hop no longer relies on it |
| Tier 6 | 3, and ideally 8-10 | An apply needs an explicit target state, and the emit sits on the write pipeline |

**Card 1 carries more weight than its row suggests.** Five things hang off it: the wrong-worktree
write itself, the largest single size saving in the product (the absolute path repeated three to
five times in every result), the workspace-key anchor, the worker's working directory and with it
the worktree lock, and a mis-route failing loudly instead of writing a plausible file. It was
already first on value per unit of effort; it is now first by a wide margin.

### How the pit-of-success inversions relate to the cards

Roughly fifty inversions are proposed across the eight files. They are not a separate workstream,
and three-quarters of them need no separate card, because they fall into three relationships:

- **The card *is* the inversion.** Cards 1, 3, 9 and 10 are the four biggest inversions written as
  work: a path type that cannot be resolved without a base, a target-execution union, one write
  pipeline, one symbol resolver. Doing the card badly and doing the inversion are the same
  alternative, so there is nothing extra to schedule -- only a note in the card that the mechanism,
  not the fix, is the deliverable.
- **The inversion is a guard that must land *before* its cards**, because the cards are exactly the
  work it protects. These are cheap, ungated and few. They were Tier 0, and are done (PR #295).
- **The inversion only exists once its card does.** One framed message type for every pipe needs the
  pipe work; notice discipline needs somewhere to live (card 9). These follow and should be written
  into the card that creates the home, not tracked separately.

### ~~The pattern four reviewers found separately: computed, returned, never consumed~~

Four subsystems computed a fact carrying a docstring that argued why a reader needs it, and consumed
it nowhere; four reviewers found it without looking for the same thing. **Closed as a pattern,
PR #295.** `ProducedFactTests` is the guard and its summary carries the reasoning, its exemption
list is the worklist, and an empty list is the definition of done. The four instances: `HostVersion`
fixed by card 0d, the workspace-key anchor at 11c, the reference facets at 11e, the window facts
at 22.

### Tier 1 — wrong answers and wrong side effects

These produce confident wrong results today. Everything else is cost.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| 1 | **A relative path resolves against the broker, so a write lands in another worktree.** Rebase hints against the caller's origin before ranking; make it a type so it cannot recur. Make the broker-to-worker hop absolute-only so a mis-route fails loudly instead of writing to a plausible file. | BRK-01, AGT-10, BRK-20 | #214 | M |
| 1b | **A warm worker pins its worktree directory open**, so `git worktree remove` fails naming a process nobody can see. Falls out of card 1: once the hop is absolute-only the worker's working directory stops being load-bearing and can move somewhere inert. Cut with #157, since an evicted worker releases the directory too. | BRK-20 | #157 | S |
| ~~2~~ | ~~A breakpoint hit is attributed by method token alone.~~ **Done, PR #270.** `BreakpointTable.Claim` matches on the IL offset — not the breakpoint object, which the card asked for and which ClrDebug's `ComWrappers` do not promise. | LIV-03 | — | — |
| ~~3~~ | ~~A dead target reports as stopped.~~ **Done, PR #265.** `TargetExecution` + `StopRecord`. HOT-06's remaining half — the `Applying(ApplyRecord)` arm — is *not* done and belongs with tier 6 card 32, which is the first card that has an apply to have a state for. | LIV-02 | — | — |
| 4 | **A timed-out XAML request still runs in the app** — reported failure, did the thing anyway. This is #208's real cause, and it is in the product, not the test. | UIP-15, LIV-07 | #208 | M |
| 5 | **The tap's request side does not escape what its reply side unescapes.** A tab or newline in a property value mis-frames the edit and mis-keys its status, so an edit that landed reports as not applied. | IPC-01 | new | S |
| 6 | **One compilation is asked about another's symbol**, leaking a Roslyn error naming an argument the caller never sent, from three tools. | WRK-06 | #121, #212 | S |
| ~~7~~ | ~~The analyzer loader flattens every analyzer into one load context.~~ **Done, PR #269.** One `AssemblyLoadContext` per analyzer directory, `AnalyzerVersionIsolationTests`, and the rule written into `docs/invariants/analyzers-and-generators.md`. | WRK-08 | — | — |

### Tier 2 — the three structural refactors

Each closes a class of bug rather than a bug, and each is a prerequisite for something else.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| 8 | **Draw the text/syntax line once in the writing stack.** Syntax in, syntax out; text only inside the whitespace pass; one trivia pass after the formatter replacing five string re-indenters. **Six open fidelity issues close as a consequence.** | WRK-02, WRK-03, WRK-19, AGT-17 | #195 #197 #199 #200 #217 #218 | M-L |
| ~~9~~ | ~~Make the write pipeline a type.~~ **Done, PRs #275 #276 #278.** `EditPipeline`, all six services, `Listed` declared once. It was bigger than the card: **four tools were reporting a project clean while the caller's errors sat in it**, which the review did not find. **WRK-23 survives** — it is the *tool* layer and the pipeline is the *service* layer, so it needs a card of its own rather than falling out of this one. | WRK-01 | — | — |
| 9b | **Route every mutation tool through `RunAsync`.** Seven tools inline the same `WorkProgress.Split` / `sharedWork.Follow` / `SessionAsync` / `MutateAsync` preamble that `RunAsync` already wraps; add `ReadAsync` so the `Follow` handle cannot be forgotten on reads either. Was assumed to disappear with card 9 and did not. | WRK-23 | new | S |
| 10 | **One compilation-backed symbol resolver.** Today two-and-a-half resolvers disagree, so positional record properties are unaddressable when the name is common, and a metadata symbol is unreachable if any source symbol shares its leaf name. Every DTO in `Contracts` is a positional record. | WRK-04, WRK-05, WRK-14, AGT-03 | #233 #210 #239 | M |

### Tier 3 — make Rose win against grep

Highest leverage on adoption. Cheap relative to impact.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| 11 | **Result size discipline, reads.** Split the location shape so a listed member does not carry a declaration record; stop repeating the absolute path per hit; make `includeSignatures=false` actually remove the signature; mark generated members and honour `filePath` on code-behind. | AGT-01, AGT-02, AGT-06, AGT-11, UIP dogfooding | #234 | M |
| 11b | **Result size discipline, writes.** A write result is ~4,000 characters of which ~85% is the caller's own diff echoed back, a notice that fires on every call, or a fact already stated. Drop the diff to a range plus a normalisation line, condition the constant notices, say each fact once, name the path once. Thirteen writing tools share the base record. **Gated on card 1** for the path half (returning relative paths makes agents send them); the notice half is **now unblocked** — card 9 shipped, and `EditPipeline.Report()` is the one place a notice is decided. Apply card 9's own rule when trimming: a line stating *which* compile ran is a fact and stays. | AGT-21 | new | M |
| 11c | **Accept `workspaceKey` as an anchor wherever `workspace` is accepted.** Its own summary calls it "fit for a caller to quote back" and cites the six-worktree case; every result carries it and nothing reads it. Sixteen characters an agent will actually echo, where a sixty-character absolute path is what it drops. Makes the relative-path round trip unambiguous by construction. | AGT-21, BRK-01 | new | S |
| 11d | **Let a plural intent be one call.** The four debug bookkeeping tools take one location each, so instrumenting a code path is six model turns and six result envelopes; the alternative they are pitched against, adding log statements, is plural in one edit. Take an array, return per-item outcomes copying `LiveXamlApplyResult`, never fail the batch for one item. Read tools follow after card 11. | AGT-22 | new | M |
| 11e | **Answer an overflow with a grouping, never a bigger artefact.** Every reference already carries its containing member, project, test-ness and generated-ness, and the tool filters on one of the four. On overflow return the shape ("412: 380 in tests, 6 members") plus the narrowing vocabulary, and accept as a filter every facet already returned. A spill file only when the caller names one. | AGT-23, AGT-06, AGT-05 | #234 | M |
| 12 | **An unknown argument is dropped in silence**, then the error reports the value as missing. Collect undeclared arguments and name them. | AGT-08 | #249 | S |
| 13 | **No error should name a CLR or Roslyn concept the caller did not send.** One boundary rewrite; refusals carry advice that would actually work. | AGT-04, WRK-07, AGT-05 | #121 #210 | M |
| 14 | **Diagnostics never say the workspace is degraded**, so a clean answer from a broken workspace reads as a clean bill of health. Stamp it where attribution already happens. | AGT-12, USE-01 | new | S |
| 15 | **`rose_find_implementations` cannot be restricted to your own solution**, so a common framework interface returns 116 metadata matches truncated at 40. Also: a property's definition is listed three to four times. | IPC dogfooding, USE dogfooding, AGT-07 | new | S |

### Tier 4 — mechanism: make the rules structural

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| 16 | **Put the XAML and tap half into CI**, the part card 0a leaves out: a C++ toolset, the Windows App SDK and developer mode. Three invariant documents are review-only until this lands. | UIP-25 | new | L |
| 17 | *(moved to card 0a -- the accidental inclusion turned out to be proof that debugger tests run fine on a hosted runner, so it is widened deliberately rather than reverted.)* | UIP-14 | new | -- |
| 18 | **Share fixtures on the Roslyn half.** 254 solution loads and 299 fixture copies over six fixtures, with a proven sharing model already in use next door. Issue #39 understates it by four times. | UIP-13 | #39 | L |
| 19 | *(moved to card 0d -- it is worth having before the work starts, not after.)* | IPC-02, BRK-05 | new | -- |
| 20 | **A correlation id on every internal hop**, into every log line. Today a failure cannot be traced across the four processes it crossed. | BRK-15, IPC-07 | new | M |
| 21 | **Guard the remaining arrangements**, after cards 0c and 0e take the two urgent ones: a stdout test of its own, a shared layout manifest, and tap tier purity checked rather than described. **The layout half has got sharply more urgent** — PR #277 took the parties to the layout from two to five, two of them packaged content a user runs, so a layout change now fails at install time on somebody else's machine rather than in CI. Worth splitting out and pulling forward. | UIP-18, UIP-22, UIP-24 | new | M |

### Tier 5 — re-aim the UIs

Commit to the supervising user, or decide not to. Everything here follows from that call.

| # | Card | Findings | Issues | Effort |
|---|---|---|---|---|
| 22 | **Render the facts already computed for a window.** Degraded reasons with their remedies, analyzer load failures, per-project health, restore state, information age, session notice. The cheapest wins in the repository. | USE-01, USE-03 | new | M |
| 23 | **Show what the agent is doing, in the inspector.** The data is already on the object the window holds; the tray renders it and the inspector does not. | USE-02 | new | S |
| 24 | **The activity log is the only record of what an agent did to your solution.** It is eight entries, collapsed, tertiary grey, dropped on close. Persist it, give it client attribution, promote it. | USE-04, USE-05 | new | M |
| 25 | **Make facts copyable.** Nothing in a window whose job is feeding facts to an agent can be copied except one XAML address. | USE-09, USE-14 | new | S |
| 26 | **Cut what earns less than it costs**: the threads pane (the only pane that freezes the user's app as a side effect of being visible), the duplicate tray menu, the empty title bar, the load time on the permanent facts line. | USE-07, USE-15, USE-17 | new | M |
| 26b | **Write down why the magnifier exists.** The OS magnifier filters bilinearly and cannot be told not to, so it can neither read an exact colour nor show a one-pixel gap at a corner radius. That reason is in no comment, invariant or wiki page, and this review recommended deleting the feature before being corrected. A header sentence, a decision record, and a tooltip that states the benefit rather than the mechanism. | USE-08 | new | S |
| 27 | **Connect the pick to the window that explains it.** Six manual steps today. | USE-10 | #226 | L |

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

1. Compact `rose_outline` is not compact, and loses to grep on both size and information (AGT-01).
2. `includeSignatures=false` leaves the declaration text in `preview` (AGT-01).
3. `rose_outline` on a WinUI code-behind merges the generated partial, marks none of it generated
   despite the description's promise, and ignores `filePath` (05 dogfooding).
4. A metadata symbol is unreachable whenever any source symbol shares its leaf name (AGT-03).
5. A positional record property cannot be addressed by name from any tool (WRK-04, beyond #233).
6. `rose_find_implementations` has no way to ask "in my solution" (IPC dogfooding).
7. `rose_find_references` lists one definition three to four times at different columns (USE dogfooding).
8. `definitionsOnly=true` reports `truncated: true` over an empty list (AGT-05).
9. `rose_resolve_name` without a file path fails with a leaked Roslyn parameter name (AGT-04).
10. `rose_build_freshness` counts `obj/` artefacts as sources, so it can name a generated editorconfig
    as the newest source file (HOT-10).
11. `rose_symbol_info` returns `"source":[]` when source was not requested, which reads as "no source".
12. No tool reads XAML, though the worker knows a great deal about it.
13. No way to ask Rose what its own tool listing looks like to a client, so surface changes are
    reviewable only as pass/fail.
14. No negative-space or bulk query ("which members of this type does nobody reference"), which sent
    two reviewers to grep. **Partly answered by `rose_find_split_options` (PR #290)**, which reports
    which members would move together, from field co-occurrence and call-graph dominance. It is the
    first tool here that answers a question about a type rather than about a symbol, and it has already
    chosen two refactors (#287, #291) — in #291 it rediscovered, from state alone, a boundary that two
    other files described in prose and it had never read. It does not answer the negative-space
    question above: "nothing references this" was still established by hand with `rose_find_references`
    while writing #274.

## Suggested reading order for splitting cards

1. This file.
2. `08-ui-usability.md` sections 1 and 3, and the supervision thesis. It is the strategic decision;
   Tier 5 depends on it and nothing else does.
3. `07-hot-reload-readiness.md` "What exists today" and the milestone table.
4. `02-worker-roslyn.md` findings WRK-01, WRK-02, WRK-04. They are Tier 2 entire.
5. `05-ui-tests-and-process.md`'s invariant-to-test table. It is the map for Tier 4.
6. The rest as the cards demand.
