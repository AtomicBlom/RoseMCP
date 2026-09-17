# Fable review 2 -- brief and index

Review of RoseMCP as of 2026-09-16, at 446 commits. The output divides the work and cuts the cards.

## How this review gets implemented

`feature/fable-review` is the integration branch and carries these documents.

- Each piece of work **branches off `feature/fable-review`** and **pull-requests back into it**, not
  into `main`. So the review and the work that answers it stay together, and a card can be reviewed
  against the finding that asked for it.
- When the review has been worked through, **one final pull request takes `feature/fable-review`
  into `main`**.
- Work proceeds **tier by tier, in the order the card list in [`00-overview.md`](00-overview.md)
  sets out**, starting with tier 1. The tiers are ordered by value per unit of effort, and tier 2
  and tier 6 each have prerequisites named in their own rows.

A card that turns out to be wrong is worth more than a card that is merely done: amend the finding
in its file in the same pull request, the way USE-08 was amended when the magnifier's real reason
came to light. The findings are a snapshot of what eight reviewers could see from the code, and the
person implementing one knows more than the reviewer did.

### These documents shrink as the work lands

Six thousand lines of review is a cost every future session pays to read. So a pull request that
closes a card **collapses what it closed**, in the same commit as the fix. The review is scaffolding,
not a record: it should be smaller after every merge, and empty by the end.

- **In [`00-overview.md`](00-overview.md)**, strike the card's row and leave one line: what shipped
  and where. The card list is what a new session reads first, so it must say the state of the work
  without anything else being opened.
- **In the finding's own file**, replace the finding with two or three lines -- the claim, the commit
  or pull request, and where the reasoning now lives. Delete the What, the Why it matters and the
  Suggested change. They were arguments for doing the work, and the work is done.
- **Move the reasoning before deleting it.** A finding usually contains the "why this and not that"
  that belongs in a comment beside the code or in `docs/decisions/`, which is where this repository
  keeps such things permanently. Migrating it is part of closing the card, not a follow-up. A
  finding that is deleted without its reasoning being rehomed has thrown away the expensive half.
- **A whole file that is fully closed** collapses to its heading, its verdict and its Strengths
  section. The strengths are the part worth keeping longest, because they say what a later refactor
  must not break.
- **When every tier is closed**, `docs/fable-review-2/` should hold little more than this README, and
  the final pull request into `main` deletes it. What survives is in the code, in the tests, in
  `docs/decisions/` and in `docs/invariants/`, which is where it can be maintained.

Findings that are deliberately not being done are the exception: keep those in full, with a line
saying why they were declined. An un-actioned finding with no decision recorded against it is the
thing a future review will re-derive from scratch at full cost.

## Questions the review answers

1. How maintainable is the code?
2. How well is it architected?
3. Which patterns can be inverted into a pit of success (make the right thing the only thing)?
4. Is RoseMCP a good citizen in agentic flows?
5. What could be done better?
6. Are the technologies and protocols used for IPC appropriate?
7. How far is the codebase from real C# hot reload?
8. The project grew from "Roslyn workspace MCP" into a XAML tap and a full debugging suite, largely
   vibe-coded. Has enough design care gone in for that growth to hold?

## Out of scope

- Files over 1000 lines, especially in the tests. A separate session is splitting those already.
  Do not raise file length as a finding. Raise *cohesion* problems if the file is long for a reason
  other than "it grew".

## Files

| File | Covers |
|---|---|
| `00-overview.md` | Synthesis: answers to the eight questions, ranked card list |
| `README.md` | This brief: format, ground truth, status |
| `01-broker-and-server.md` | `RoseMcp.Broker`, `RoseMcp.Server`, `RoseMcp.Solutions`, `RoseMcp.Settings`, `RoseMcp.Logging` |
| `02-worker-roslyn.md` | `RoseMcp.Worker`, `RoseMcp.XamlStubs` |
| `03-liveapp-debugger-and-tap.md` | `RoseMcp.LiveApp`, `RoseMcp.Symbols`, `RoseMcp.XamlDiff`, `RoseMcp.Xaml.*Tap` |
| `04-agentic-citizenship.md` | The tool surface as an agent sees it: names, descriptions, argument shapes, errors, results, progress |
| `05-ui-tests-and-process.md` | `RoseMcp.Tray`, `RoseMcp.Inspector`, `RoseMcp.Ui*`, test architecture, CI, docs as a maintenance system |
| `06-ipc-and-protocols.md` | Every process boundary and the protocol crossing it |
| `07-hot-reload-readiness.md` | Distance to C# hot reload, and the path |
| `08-ui-usability.md` | Product review of the three UIs: tray, inspector, in-app Rose panel. Usability and whether each earns its place. Not a code review; `05` covers the code. |

## Report format (every file)

```
# <Title>

**Scope.** Projects and files read.
**Verdict.** One paragraph. Say the grade out loud: strong / adequate / fragile, and why.

## Strengths
What to keep. Concrete, with file references. This section is not optional: a card list that only
lists problems loses the things that must survive a refactor.

## Findings
One heading per finding, numbered with a prefix for the file (BRK-01, WRK-01, LIV-01, AGT-01,
UIP-01, IPC-01, HOT-01).

### <PREFIX>-NN <one-line claim>
- **Severity:** High / Medium / Low   (High = wrong answers, data loss, or a design that blocks a stated goal)
- **Effort:** S / M / L
- **Where:** `path/File.cs:line`
- **What:** the defect or smell, concretely.
- **Why it matters:** the failure it produces or the cost it imposes.
- **Suggested change:** what to do instead. Name the pattern.

## Pit-of-success inversions
Patterns that today depend on a reviewer remembering a rule, and how to make the rule structural
(a type, an analyzer, a test, a registration path, a DI shape). Each with: rule today -> mechanism.

## Open questions for Steve
Anything the reviewer could not resolve from the code or docs.

## Rose dogfooding notes
Every time a `rose_*` tool was reached for: which tool, what for, whether it worked, and if it lost
to grep/Read why. A tool that was not reached for when it should have been is also a note.
```

## Ground truth gathered before the agents ran

- Production code is roughly 60k lines across 18 projects; tests roughly 31k lines.
- `rose_workspace_status` on this repository reports **Degraded**: `Microsoft.Extensions.Logging.Generators.dll`
  and `Microsoft.Extensions.Options.SourceGeneration.dll` fail to load (manifest version mismatch,
  10.0.14 located vs the pinned 10.0.11), and the WinUI projects log "Cannot resolve Assembly or
  Windows Metadata file ... RoseMcp.Contracts.dll" during the design-time build.
- No `TODO`/`HACK`/`FIXME` markers in `src`. 51 issue-number tags in comments (#171 tracks the migration).
  2 warning suppressions. 23 files use a lock, semaphore, `Interlocked`, `Channel` or a concurrent collection.
- No `Console.Write` in `src` (the stdout rule holds).
- 46 open GitHub issues; 17 labelled `dogfooding`, 12 `tool-surface`, 8 `live-app`.

## Status: complete (2026-09-17)

All nine files written. `00-overview.md` has the answers to the eight questions, the ranked card
list in six tiers, what must survive a refactor, and the list of Rose defects this review found by
using Rose.

| File | Findings | High | Grade |
|---|---|---|---|
| `01-broker-and-server.md` | 19 | 2 | Adequate; strong core, fragile lifetime and seams |
| `02-worker-roslyn.md` | 23 | 5 | Core strong, editing stack fragile |
| `03-liveapp-debugger-and-tap.md` | 22 | 2 | Adequate leaning strong |
| `04-agentic-citizenship.md` | 20 | 6 | Adequate, self-aware |
| `05-ui-tests-and-process.md` | 27 | 3 | Strong; one structural hole, one growing debt |
| `06-ipc-and-protocols.md` | 10 | 1 | Adequate tending strong |
| `07-hot-reload-readiness.md` | 12 | 4 | Fragile but well-aimed; 5-6.5 weeks to v1 |
| `08-ui-usability.md` | 17 | 3 | Adequate, aimed at the wrong job |

150 findings: 26 High, 78 Medium, 46 Low. Roughly 6,000 lines of review.

**Operational note for a future run:** a reviewer reading a 20k-line project in full costs 400-500k
tokens. Four in parallel plus the parent exhausted a session limit on the first attempt. Tell each
reviewer to write its file incrementally (header and Strengths first, findings appended) so an
interruption costs nothing, and give exact file paths rather than folder hints.
