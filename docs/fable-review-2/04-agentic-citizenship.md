# Agentic citizenship -- the tool surface as an agent sees it

**Scope.** `src/RoseMcp.Contracts/ToolDescriptions.cs`, `ToolNames.cs`, `ToolArgumentShape.cs`,
`ArgumentValues.cs`, and the result DTOs (`OutlineResult.cs`, `SymbolInfoResult.cs`,
`ReferencesResult.cs`, `MemberEditResult.cs`, `DiagnosticsResult.cs`, `LiveDebugEventPage.cs`,
`LiveXamlTree.cs`). The tool layers: `src/RoseMcp.Broker/Tools/*.cs`, `src/RoseMcp.Worker/Tools/*.cs`,
`src/RoseMcp.LiveApp/Tools/*.cs`, the three `ToolErrorReporting.cs`, `src/RoseMcp.Broker/ForwardedError.cs`,
`ToolListing.cs`, `ServiceCollectionExtensions.cs` (server instructions). The surface tests:
`ToolDescriptionTests.cs`, `ToolSurfaceTests.cs`, `ToolBudgetTests.cs`, `ToolArgumentShapeTests.cs`,
`ToolParityTests.cs`. The 17-issue `tool-surface`/`dogfooding` corpus. Plus ~30 live calls against
this worktree's own loaded solution, transcribed below.

**Verdict.** **Adequate, and unusually self-aware about being adequate.** The prose on this surface
is the best tool-description writing I have read in an MCP server: every description names the
alternative it beats, and the class doc on `ToolDescriptions` states the thesis outright -- "an agent
reaching for grep is not choosing badly between known options, it is not aware there was a choice"
(`src/RoseMcp.Contracts/ToolDescriptions.cs:14-15`). The *decision* layer is strong. What is fragile
is the layer under it: **the results are too big, and the errors are inconsistently good.** Compact
`rose_outline` on a 441-line class costs 13x what `grep -n public` costs and full mode 30x
(transcript T1), so the tool that exists to replace a file read is the one an agent learns to
stop calling. `rose_symbol_info` on a referenced assembly works or does not depending on whether any
unrelated source symbol happens to share the *leaf* name -- `Microsoft.CodeAnalysis.Workspace.CurrentSolution`
answers, `System.Collections.Generic.List` does not, because `SolutionFileReader.List` exists
(T3b). And one tool whose whole job is unsticking a caller replies with a Roslyn parameter name
the caller never sent: `rose_resolve_name` fails with "Parameter 'symbol' must be a symbol from this
compilation" (T4a). The surface is designed by someone who understands the agentic contract
better than most; it is *implemented* by a codebase three and a half weeks old, and the gap shows
in size discipline and in the error paths that were not the happy one.

Grade by family: **navigation strong**, **writing tools strong on paper and unverified in practice
by this reviewer** (read-only brief), **errors mixed**, **result sizes weak**, **progress and
long-operation handling strong**, **multi-agent story thin**.

## Strengths

**1. Every description names what it beats.** This is the single most important property of a tool
surface an agent reads once, and it is present on nearly all 51. `rose_replace_body`:
"Use it rather than a line-range edit, which is how a member gets broken -- splicing against moved
line numbers drops a brace, and the damage is found at the next build"
(`src/RoseMcp.Contracts/ToolDescriptions.cs:680-682`). `rose_find_implementations`: "Grep cannot
answer this at all: an implementation need not mention the interface's name anywhere near the
member" (`:586-587`). `rose_add_file`: "Use it rather than writing the file with a text tool -- that
is what starts most work, and so the earliest place a session stops being able to ask semantic
questions at all" (`:788-790`). An agent does not have to infer the value proposition; it is stated.

**2. One description constant, shared by both hosts, with a parity test.**
`ToolDescriptions` exists because five descriptions had drifted and in every case the broker's -- the
one a client reads -- said less (`src/RoseMcp.Contracts/ToolDescriptions.cs:6-11`).
`tests/RoseMcp.IntegrationTests/ToolParityTests.cs` now asserts the two ends declare the same
arguments, and `ToolNames.LiveAppPairs` (`src/RoseMcp.Contracts/ToolNames.cs:268-286`) exists purely
so the live-app half, which does not pair by name, can be asserted too. Drift between "what the tool
says" and "what the tool does" is the failure mode that quietly kills adoption, and it has a test.

**3. Addressing by name rather than by line, everywhere, uniformly.** `SymbolArgument` is one
constant used by every symbol-addressing tool (`:25-28`), and the reason is written down in
`SymbolTarget`'s doc: "A wrong position is worse on a read than on a write, because it is silent"
(`src/RoseMcp.Worker/SymbolTarget.cs:10-13`). For a batch of renames this is not a nicety -- a line
number found by reading is wrong the moment an earlier rename in the same batch lands, and
`rose_rename_symbol`'s description says exactly that (`ToolDescriptions.cs:616-618`).

**4. Enum-like strings refuse rather than default.** `ArgumentValues`
(`src/RoseMcp.Contracts/ArgumentValues.cs`) exists because four string enums had a default arm that
turned a typo into a confident answer to a different question -- `scope: "proj"` analysed the whole
solution, `minimumSeverity: "warn"` reported warnings when errors were wanted, a misspelt event kind
widened the filter instead of narrowing it, and any step mode but `in`/`out` stepped over. The
refusal names the argument, what arrived, and every value that would have worked
(`ArgumentValues.Unknown`, `ArgumentValues.cs:30`). This is the correct shape and it is centralised so it cannot be
forgotten on the next enum.

**5. `ToolArgumentShape` turns a binder error into an actionable one.** The SDK's own message is
"The JSON value could not be converted to System.String[]. Path: $", which names a CLR type the
caller never wrote and points at the document root. `ToolArgumentShape.Mismatch` reads the tool's own
input schema and answers "usings takes a list of strings, and a string was sent. Send it as
[\"one\", \"two\"]" (`src/RoseMcp.Contracts/ToolArgumentShape.cs:57-59`). It runs only after the
binder has already refused, so it can never turn a working call into a refusal (`:16-21`). That is
the right layering, and it lives in `Contracts` because there are three MCP boundaries and one is in
an assembly the tests cannot reference.

**6. The near-miss suggestion on a misspelt symbol is excellent.** Sending
`RoseMcp.Broker.WorkspaceManagr.CallAsync` (one letter wrong) comes back with "'CallAsync' is
declared as RoseMcp.Broker.WorkspaceManager.CallAsync, RoseMcp.Broker.WorkspaceWorker.CallAsync"
(`src/RoseMcp.Worker/DeclarationLocator.cs:268-271`) -- the answer contains the fix, verbatim, ready
to paste into the retry. Most servers say "not found".

**7. `rose_search_symbols` returns the exact string the next call wants.** Every match carries an
`address` field (`RoseMcp.Worker.ToolErrorReporting`) which is precisely what `symbol` takes on
`rose_symbol_info`, `rose_find_references` and every write tool. The two-call loop
search -> act needs no string surgery in between. `rose_symbol_info` and `rose_outline` echo the
`address` too, so a result is always re-addressable.

**8. `rose_workspace_open` returns at once and says so in the first clause.** "Starts loading a
solution and returns within about a second, without waiting for the load. **Never required**"
(`ToolDescriptions.cs:513-515`). Both halves matter: an agent that would otherwise treat a 20-second
first call as a hang is told the shape, and an agent that would otherwise emit a setup call before
every session is told not to. The README repeats it: "That is the whole setup. There is no
`workspace_open` to call first" (`README.md`, Install).

**9. Refusals that would produce a silent wrong answer are refusals, not guesses.** An ambiguous
overload is refused with both candidates and their lines rather than one picked
(`DeclarationLocator.Ambiguous`); `rose_resolve_name` "Two candidates are both returned, never a
first pick: the wrong import compiles and binds to the wrong type" (`ToolDescriptions.cs:828-829`);
`rose_delete_member` refuses an ambiguous name because "that is the deletion with no symptom at
all -- it compiles, and the behaviour that was meant to change did not" (`:777-780`);
`rose_set_attribute action=set` refuses where several attributes of that name exist because "four
InlineData attributes is the ordinary shape of a test" (`:816-819`). The taste here is consistently
right.

**10. The server `instructions` block is a routing table, not a sales pitch.** It is grouped by the
*question the agent has* ("find a declaration", "does it compile", "usages") rather than by tool
family, which is the order an agent actually reaches in, and it ends with the two sentences that do
the work: "Grep matches comments, strings and same-named identifiers, and misses overrides and
interface implementations" and "No setup call". See `src/RoseMcp.Broker/ServiceCollectionExtensions.cs`.

**11. Size controls exist on every list tool and default sensibly.** `maxResults` on
`rose_find_references` (200), `rose_diagnostics` (200), `rose_search_symbols` (50),
`rose_resolve_name` (20), `rose_find_implementations` (200), `offset`/`limit` on `rose_xaml_tree`,
`maxEvents` on `rose_debug_events` (500). The argument is named `maxResults` on all of them, which
is one word to remember rather than six. Results carry `totalCount` and `truncated`.

**12. `rose_debug_events` has a wait primitive, so the agent does not spin.** `waitSeconds` "Seconds
to wait for a matching event rather than returning what is there now; 0 answers at once. Use it with
kinds to wait for one thing instead of polling. Capped at 60" (`ToolDescriptions.cs:459-461`), and
the doc comment says why the cap and why an empty wait loses nothing: "events are buffered and the
same cursor picks up whatever arrives next" (`:456-457`). A cursor plus a bounded blocking read is
the correct shape for an agent watching a process, and most debug-over-MCP designs get this wrong.
**Amended by #300:** the shape was right and the default was not. `after=0` meant "anything, ever",
so an agent that set a breakpoint and then waited from the default could be handed a hit from before
it set one, in a page indistinguishable from the one it was waiting for. Every answer now hands back
the position the stream stood at, so the cursor a caller needs is the one already in its hand.

## Transcripts

Every call in this review ran against this worktree's loaded solution (`RoseMcp-e5ce8a33`,
revision 1). Sizes are the raw JSON as it arrived.

| # | Call | Result | Size | Verdict |
|---|---|---|---|---|
| T1a | `rose_outline symbol=RoseMcp.Broker.WorkspaceManager includeSignatures=false includeDocumentation=false` | 24 members | **~10.1 KB** | answered; 13x the cost of the grep |
| T1b | `rose_outline symbol=RoseMcp.Broker.WorkspaceManager` (full) | 24 members | **~22.3 KB** | answered; 30x the grep |
| T1c | `grep -n "public\|internal" src/RoseMcp.Broker/WorkspaceManager.cs` | 11 lines | **748 B** | answered the question I had |
| T2 | `rose_symbol_info symbol=Microsoft.CodeAnalysis.Workspace.CurrentSolution` | property, kind, doc, `isFromSource:false` | 1.1 KB | **worked** -- metadata read, exactly as advertised |
| T3a | `rose_symbol_info symbol=ModelContextProtocol.Server.McpServer.SessionId` | error | -- | **failed**: "Nothing is declared at ... 'SessionId' is declared as RoseMcp.Broker.LiveAppSession.SessionId, [4 more]" |
| T3b | `rose_symbol_info symbol=System.Collections.Generic.List` | error | -- | **failed**: the suggestions were four unrelated methods named `List` in Rose's own source |
| T3c | `rose_symbol_info symbol=ModelContextProtocol.Server.McpServerTool` | type + full XML doc | **~9.2 KB** | worked, at the price of the entire raw `<member>` blob |
| T4a | `rose_resolve_name name=ToolErrorReporting` (no `filePath`) | error | -- | ~~**failed**: `Parameter 'symbol' must be a symbol from this compilation or some referenced assembly. (Parameter 'symbol')` -- #121 reproduced, on a tool that has no `symbol` argument~~ **#306, #431.** |
| T4b | `rose_resolve_name name=ToolErrorReporting filePath=src/RoseMcp.Broker/WorkspaceManager.cs` | 1 candidate + "in scope already, so the error is something else" | 700 B | **excellent** |
| T5 | `rose_find_references symbol=RoseMcp.Contracts.ToolNames.WorkspaceStatus includePreviews=false` | 16 hits, `truncated:false` | 4.1 KB | worked; flat list, absolute path repeated 17 times |
| T6 | same, `definitionsOnly=true` | `references: []`, `totalCount:16`, **`truncated:true`** | 600 B | ~~misleading -- see AGT-05~~ **#378.** |
| T7 | `rose_search_symbols query=ToolErrorReporting` | 3 matches, each with an `address` | 1.6 KB | **excellent**; the address is the next call's argument |
| T8 | `rose_symbol_info symbol=RoseMcp.Broker.WorkspaceManagr.CallAsync` (typo) | error naming both real `CallAsync` declarations | -- | **excellent**; the fix is in the message |
| T9 | `rose_outline symbol=... workspace=C:\Windows\System32` | "No solution or project found at or above 'C:\Windows\System32'. Pass the path to a .sln, .slnx, or .csproj." | -- | **excellent** |
| T10a | `rose_diagnostics filePath=src/RoseMcp.Broker/WorkspaceManager.cs` | 0 diagnostics | 240 B | sub-second |
| T10b | `rose_diagnostics` (whole solution, 18 projects) | 0 diagnostics | 240 B | a few seconds against a `dotnet build` of 30-60 s. **The single strongest win on the surface.** |

## Findings

### ~~AGT-01 `rose_outline`'s compact mode is not compact, so the tool loses to `Read` on exactly the files it exists for~~
**#374.** Every outlined member carried a whole declaration record, so a compact outline cost more than
a grep of the file and a large type overran what a client accepts. A member is its name, kind,
accessibility and line, and what a type's members share is said once.

### ~~AGT-02 `includeDocumentation` returns the whole summary, where the schema promises "the first line"~~
**#374.** An outline's documentation was the whole summary where its help promised one line. It is the
summary's first sentence, rendered, and the help says so.

### ~~AGT-03 A metadata symbol is unreachable whenever any source symbol anywhere shares its *leaf* name~~
**#418.** Whether a read could reach a referenced assembly turned on the last segment of the name. A
read asks metadata whenever source has nothing at the address, and says when it did.

### ~~AGT-04 A leaked Roslyn exception names an argument the tool does not have~~
**#306, #431.** A Roslyn exception reached callers naming a parameter the tool did not have, and read
as advice about their arguments. No refusal at any boundary carries a CLR parameter name, and an
exception that escaped a framework says whose failure it is.

### ~~AGT-05 `definitionsOnly=true` reports `truncated: true` over an empty list~~
**#378.** Asking for the count alone reported the list as truncated, which no retry could change.
Truncation means only that raising the cap lists more.

### ~~AGT-06 `rose_find_references` promises grouping and returns a flat list with the absolute path repeated per hit~~
**#378, #374.** References were a flat list with the absolute path on every hit. They are listed by
file, each path relative to the caller's directory, which is named once.

### ~~AGT-07 `rose_find_implementations` has no `project` filter, so the question it advertises is the one it cannot answer~~
**#383.** A framework interface was answered with its implementations across every dependency, and
the solution's own were cut off behind them. It lists this solution's source only, counts what it
left out, and narrows by `project` the way `rose_find_references` does.
**Declined:** an argument to list the dependencies' implementations as well. The surface had no room
for one under its budget, and no caller has asked that question of this tool.

### ~~AGT-08 A misspelled argument is dropped in silence and the error then reports the value as missing~~
**#249.** An argument sent under a name the tool did not declare was dropped, so a refusal reported
as missing a value the caller had sent and a call that succeeded answered a different question.
Both now name the argument and the declared name it most likely meant; the further aliases this
suggested were declined, because an alias teaches nobody the real name.
**Still open, awaiting a decision:** making one word mean imports everywhere. `rose_add_using` takes
`namespaces` where every other writer takes `usings`, and no open card carries the rename.

### AGT-09 Two conventions for an enum-like argument, and the better one is used on three tools
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs:42` (`InspectorVisibility showInspector`) versus `BrokerAnalysisTools.cs:26,27,325,442` and `LiveAppDebugTools.cs:434` (`string? scope`, `string? minimumSeverity`, `string scope`, `string? position`, `string mode`)
- **What:** `showInspector` is a real C# enum, so the SDK emits a JSON Schema `enum` and the client rejects a wrong value before it reaches the server. `scope`, `minimumSeverity`, `position`, `callSites`, `action`, `verifyScope`, `mode` and `kinds` are strings whose valid set lives only in the prose and in `ArgumentValues`' runtime refusal. Confirmed in the schema as sent: `"scope": {"type": ["string","null"], "description": "document, project, or solution. Defaults to solution."}` -- no `enum` key.
- **Why it matters:** `ArgumentValues` exists because four of these silently defaulted a typo into a confident answer to a different question, and it fixed that at runtime -- one wasted round trip per typo. The enum form costs zero round trips, because the constraint is in the schema the model reads while composing the call. The surface already knows how, and uses it on the three least important arguments.
- **Suggested change:** Make every fixed-set argument a C# enum, keeping `ArgumentValues` as the worker's own parse (it is driven standalone and must still refuse). Where a synonym is accepted -- `file` for `document` -- make it an enum member. Add a `ToolSurfaceTests` assertion: no advertised argument whose description names alternatives may lack an `enum` in its schema.

### ~~AGT-10 A relative path resolves against the server process's working directory, which on a six-worktree machine is a write to the wrong checkout~~
**#305.** Every path argument was documented as solution-relative and was in fact measured from the
server process's own directory, so on a machine holding several checkouts of one repository a write
landed in the wrong one and reported success. It is measured from the calling session's directory,
the argument help says so, and the hop on from there carries absolute paths only.

### ~~AGT-11 `rose_symbol_info` returns raw, unbounded XML documentation~~
**#374.** Documentation came back as the raw, unbounded XML of the comment. It is the summary as
prose, cut at a sentence past a ceiling with a notice saying so.
**Declined:** parsed `remarks`, `returns` and `params`. A symbol in source has its whole comment one
switch away in `includeSource`, and for a library symbol they would cost an argument on a surface
held to a single ceiling with no room under it.

### ~~AGT-12 `rose_diagnostics` never says the workspace is degraded, so a clean answer from a broken workspace reads as a clean bill of health~~
**#382.** A read from a degraded workspace answered with nothing to say so, so an empty list read as a
clean bill of health. Every read leads its notices with one line saying the workspace is degraded and
why, and points at status for the rest.

### AGT-13 The model-facing budget is 74 KB and is measured only for the operating system the test runs on
- **Severity:** Medium
- **Effort:** S
- **Where:** `tests/RoseMcp.UnitTests/ToolBudgetTests.cs:47` and `:104-116`
- **What:** `ModelFacing = 74000` counts descriptions plus input schemas for the tools `AddRoseMcpBroker()` registers *on the current OS*. On Linux that is 30 tools, not 51, so a Linux CI run passes a budget the Windows surface may be well over, and the number that matters is never asserted where both halves are present. Separately, 74,000 characters is roughly 18-19k tokens spent at the start of every session before a question is asked.
- **Why it matters:** The budget is the best idea on this surface -- it exists because 45 tools and 101 KB became 52 and 134 in two days with nothing measuring it -- and it guards half the number on the platform where the surface is smaller. For the cost: on a 200k-token client, Rose is ~10% of context before work begins, which is the kind of price that gets an MCP server disabled rather than debugged.
- **Suggested change:** Build the list for the assertion from both registration halves regardless of host OS (the `LiveAppDebugTools` type is present everywhere; only registration is gated, and `ToolSurfaceTests` already says so). Set separate ceilings per half so the Windows-only debug surface cannot eat the Roslyn one's budget, and add a second assertion on `ServerInstructions` plus the listing, which is the true per-session cost.

### AGT-14 "Names the alternative" is asserted for 23 tools and the other 28 are on trust
- **Severity:** Medium
- **Effort:** S
- **Where:** `tests/RoseMcp.UnitTests/ToolDescriptionTests.cs:59-81`; `src/RoseMcp.Contracts/ToolDescriptions.cs:593-597`; `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs:32-36`
- **What:** `Says_what_the_caller_would_otherwise_have_done` has 23 `[Arguments]` rows, all from the Roslyn half. The 21 live-app tools declare their descriptions inline in `LiveAppDebugTools.cs` rather than in `ToolDescriptions`, so no parity or content test reaches them. Among the unguarded Roslyn tools, `rose_search_symbols` has the shortest description on the surface and is the only read tool naming no alternative at all -- it routes to other Rose tools and never says why not grep, although it is the tool an agent reaches for first.
- **Why it matters:** The rule that makes this surface good is enforced on 45% of it, and the unenforced half is the newer, faster-growing one. `rose_search_symbols` is the entry point: if it does not win the first decision, nothing downstream gets a turn.
- **Suggested change:** Move the live-app descriptions into `ToolDescriptions` beside their arguments, which are already there. Then make the test data-driven -- every advertised tool must contain one of a small set of comparative phrases ("rather than", "instead of", "cannot", "no other way") -- with an exemption list carrying a reason, in the style `ToolSurfaceTests.Unrouted` already uses. Rewrite `SearchSymbols` to open with what it beats: grep matches the word in comments and strings and cannot rank by abbreviation.

### AGT-15 The 22 live-app tools report no progress, so a 60-second wait is indistinguishable from a hang
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs` (no `IProgress<ProgressNotificationValue>` parameter on any of the 22); compare `BrokerAnalysisTools.cs`, where all 27 take one
- **What:** Every Roslyn tool takes an `IProgress` and forwards it. No debug or XAML tool does. `rose_debug_events waitSeconds=60` blocks for up to a minute, `rose_debug_attach` starts a host process and attaches ICorDebug, and `rose_xaml_tree` injects a provider into another process and walks its visual tree on the UI thread -- all silent.
- **Why it matters:** The surface is otherwise careful here: `rose_workspace_open` returns at once and its `StillLoadingNotice` tells the caller nothing will be pushed (`BrokerTools.cs:88-92`), and `WorkspaceStatusReport` carries the activity log's percentages so a poll can watch a load. Telling slow from hung is a first-class concern in this design and the debugging half opted out of it. A client that cancels on silence kills a debugger attach that was working.
- **Suggested change:** Add the `IProgress` parameter to the live-app tools and report the phase: launching the host, attaching, binding N breakpoints, waiting with M seconds left. The wait loop in particular should tick, because its duration is the one the caller chose.

### AGT-16 `Destructive` and `Idempotent` are hand-written on 51 tools and only `ReadOnly` has a test
- **Severity:** Low
- **Effort:** S
- **Where:** `tests/RoseMcp.UnitTests/ToolSurfaceTests.cs:149-172` and `:223-229`; `src/RoseMcp.Broker/Tools/BrokerAnalysisTools.cs:315-318`, `:396-399`, `:429-432`
- **What:** All four annotations plus `Title` are set on every tool, which is better than most MCP servers manage. `Only_the_listed_tools_call_themselves_read_only` guards `ReadOnly` with an explicit list and a written reason for each borderline case. Nothing guards the other two. `rose_replace_body` declares `Idempotent = true`, which holds for the `code` payload and is doubtful for `find`/`replace` and `position` -- a second `position: "end"` appends a second copy.
- **Why it matters:** A client uses `destructiveHint` to decide whether to ask the user -- the same consent decision `ReadOnly` gets a list and a paragraph for. The hint is only load-bearing if true, and 51 hand-written booleans with no reader will drift the way the descriptions did.
- **Suggested change:** Extend `ToolSurfaceTests` with `Destructive` and `Idempotent` lists in the same shape: a name appears only by someone adding it and saying why. Then reconsider `rose_replace_body` -- idempotence is a property of a *payload* here, so either declare it `false` or split the three payloads into shapes that make the claim true.

### ~~AGT-17 Whitespace fidelity is why an agent stops trusting the writer, and `rose_format` certifies the damage as clean~~
**#333, #427.** Writes damaged layout nobody asked them to change, and `rose_format` then called the
file formatted, because neither it nor dotnet format has a rule for where a line wraps. A write names
every line it changed outside what it was asked, and `rose_format` says what it checked rather than
that a file is formatted.

### AGT-18 `rose_replace_member` cannot express extract-method, and the workaround reports an error it caused itself
- **Severity:** Medium
- **Effort:** M
- **Where:** issue #211; `src/RoseMcp.Contracts/ToolDescriptions.cs:665-676`
- **What:** Replacing one member with two is refused ("The code declares 2 members and this replaces one. Add the rest with rose_add_member"), although `rose_add_member` takes several declarations in one call. The workaround is two calls with a non-compiling state between them, and step one reports "1 error(s) introduced" -- an artefact of the tool's own sequencing. #211 records hitting it three times in one session.
- **Why it matters:** Extract-method is among the most common refactors an agent performs, and it is the one shape where the surface's headline claim -- "the result says what the edit broke" -- produces a false positive. An agent reading step one cannot tell that error from a real one without remembering it caused it, and an agent that cannot trust the introduced-diagnostics list has lost the reason to prefer these tools over `Edit`.
- **Suggested change:** #211's fix. Accept code declaring more than one member when exactly one matches the symbol being replaced, and place the rest beside it -- which is what the refusal already tells the caller to do by hand. Anything else stays refused. The asymmetry is the surprise, so removing it is also the simplest thing to explain.

### AGT-19 Three near-identical `ToolErrorReporting` copies, where the shared part is pure logic
- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/ToolErrorReporting.cs:67-74`, `src/RoseMcp.Worker/ToolErrorReporting.cs:67-74`, `src/RoseMcp.LiveApp/ToolErrorReporting.cs:68-75`
- **What:** `Named` is byte-identical in all three; `Explainable` is identical in two and inlined in the third's `when` clause. The live-app copy's doc defends it: "Nine lines in two places beats a dependency on the MCP hosting package from the type library." It is now three places and about 25 lines, and the genuinely host-specific part is two lines (the worker appends its solution path).
- **Why it matters:** This is the class the repository has already been bitten by -- `ToolDescriptions` exists because two copies of the same text drifted. A rule added at the boundary has to be added in three files, and the third is the one that gets missed.
- **Suggested change:** The decision to keep MCP types out of `Contracts` is right; the split is in the wrong place. `ToolFailure.Message(exception, tool)` in `Contracts` already decides whether an exception is a refusal or a leak, and `ToolArgumentShape.Refusal` composes the message; what is still copied is the glue between them -- `Named`, `Explainable` and the call that joins the two. Fold that into one `Contracts` function over values (the schema, the arguments, the exception, the tool name and a suffix), and leave each host the six-line filter that reads them off its own `RequestContext`. Then the rule has one home and a test.

### AGT-20 Two agents on one stdio broker share every workspace and every debug session, with nothing to tell them apart
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/CallSession.cs:14-18`; `src/RoseMcp.Broker/LiveAppSessionManager.cs:83-87`, `:156-157`; `src/RoseMcp.Contracts/ToolDescriptions.cs:77-78`
- **What:** Ownership of a live-app session is `CallSession.Id`, the MCP transport's session id, documented as "Null is the honest value for a stdio broker, which has one session for the life of the process, so every call in it owns everything it started". That is true of the *transport* and false of the *agent*: Claude Code runs subagents over one MCP connection, so N subagents share one null owner. `Find` therefore matches, and a subagent can `rose_debug_list` a sibling's session, set breakpoints in its target, evaluate inside it, or `rose_debug_detach` it. The same holds for `rose_workspace_reload` (a full design-time build, ~21 s here, invalidating every warm answer another agent holds) and `rose_workspace_close`. On the read side `revision` is per workspace and global, so two agents editing one solution see each other's revisions with no way to tell whose. `expectedRevision` is the right primitive, but its help -- "Refuse if the workspace has moved past this revision" -- never says it is what you use when something else might be writing, and the instructions never mention concurrency at all.
- **Why it matters:** Several subagents in one repository is the ordinary way this repository is worked on -- this review is four agents in one worktree -- and the only concurrency story is a transport-level notion of session that the agent layer does not correspond to. The failure is quiet: the sibling's next `rose_debug_continue` fails with a session id that no longer exists, which reads as a bug in the debugger.
- **Suggested change:** Short term, say it in the writing: make `ExpectedRevisionArgument` name the case ("pass the revision your last read returned; another agent or a human editor may have written since"), and have `rose_debug_list` mark which sessions *this call* started rather than only which it may reach. Medium term, give a call an agent identity independent of the transport -- the `_meta` channel that already carries `CallOrigin` can carry one -- and default the destructive lifecycle tools (`rose_workspace_reload`, `rose_workspace_close`, `rose_debug_detach`) to refusing a target they did not start, with an explicit `force`.

### ~~AGT-21 A write result is roughly 4,000 characters, of which about 85% is the caller's own input, a constant, or a fact already stated~~
**#375.** A write result echoed the caller's code back in a diff, named one file several times over, and
carried notices that fired on every call. It names each changed file once, relative to the caller's
directory, with the lines it changed and what it normalised, and says in a notice only what is true of
that call. The same shape is left on the read surface in one place, a `helpLink` and an absolute path
on every `rose_diagnostics` entry, which card 11i carries.

### ~~AGT-22 Tools that are plural by intent are singular by signature, and the cost is model turns rather than round trips~~
**#377.** The four debug bookkeeping tools took one location or id each, so instrumenting a path cost a
model turn per method. Each takes a list and answers every entry with its own status, and one bad
entry never fails the rest.

### ~~AGT-23 Overflow should return a smaller answer to a better question, never the same answer somewhere else~~
**#378.** An answer past its cap was the first few references and a truncation flag, and three of
the four facets on every reference could not be asked about. It is now the shape of the references,
with every facet a filter and nothing spilled to a file.


## Why tools lose to grep, ranked

From the 18-issue corpus, the three other reviewers' dogfooding notes, and my own ~30 calls. Ranked
by how often it decides a call, not by severity.

1. ~~**The answer is too big to use** (AGT-01, AGT-02, AGT-06, AGT-11, #234).~~ **#374.** The
   reads answer cheaply by default and say what a cap left out; the write results are still large
   (AGT-21).
2. ~~**The name the caller wrote cannot be addressed** (AGT-03, #210, #233, #239).~~ **#418.** The
   four instances named here resolve by name.
3. ~~**The error does not say what to do** (AGT-04, #121, #212, #210).~~ **#431.** No refusal
   names a CLR parameter, and an exception that escaped a framework says so rather than reading as
   advice.
4. ~~**The write is not trusted** (AGT-17, #195, #197, #217).~~ **#333, #427.** A write names the
   lines it changed that it was not asked to, and `rose_format` no longer calls a file clean beyond
   what it checked.
5. **The edit cannot be expressed** (AGT-18, #195, #211). Extract-method, changing a comment.
   The fallback is re-emitting the whole member, which is the read-the-file-then-edit loop the
   surface exists to remove.
6. **Habit, with no defect behind it.** Two reviewers recorded reaching for grep first and finding
   Rose would have answered (`IsTransportFailure`, the positional parameters of `SymbolLocation`).
   This is the class the server instructions and the CLAUDE.md snippet exist to fix, and the fact
   that it is *sixth* rather than first says the writing is doing its job.

Note what is **not** on the list: precision. No reviewer in this round reported a wrong semantic
answer. Every loss is about cost, reach or explanation.

## Pit-of-success inversions

**1. ~~Compact has to be measured, not intended.~~** **#295, #374.** The read and write shapes are
held to a ceiling per item, and the location record is split into the two shapes it is used as.

**2. ~~No CLR vocabulary reaches a caller.~~** **#431.** Every boundary takes CLR parameter names
out and says when an exception escaped a framework, and one test holds all three boundaries to it.
**Declined:** rewriting type names in a message. A leaked exception's are now marked as the
framework's words, and a refusal Rose wrote is its own words.

**3. ~~An unknown argument is a caller error the tool can see.~~** **#249.**

**4. A fixed set of values is a type, not a string.**
*Rule today:* remember to route the string through `ArgumentValues` rather than a `switch` with a
default (which four tools did not, and which is why the class exists).
*Mechanism:* declare the parameter as a C# enum, so the SDK puts the set in the JSON Schema and the
model never composes an invalid call. `InspectorVisibility` already proves the shape works here.
Keep `ArgumentValues` for the worker's standalone boundary, and add the `ToolSurfaceTests`
assertion: an argument whose description enumerates its values must carry `enum` in the schema.

**5. A write reports what it changed that it was not asked to change.**
*Rule today:* a reviewer reads the diff; `rose_format` is offered as the check and answers a
narrower question.
*Mechanism:* every write tool already computes a unified diff before returning. Compare the changed
line set against the span the request named; anything outside it goes in the result as
`unrequestedChanges`, and above a threshold the call refuses with `apply=false` semantics and the
diff. That one guard sits in the write pipeline rather than in each writer, so #197's reflow,
#217's re-indent and #195's deleted comment are caught by the same code and a new writer inherits it.

**6. ~~A relative path cannot be resolved without a base.~~** **#305.**

**7. Every annotation that spends the user's consent is on a list with a reason.**
*Rule today:* `ReadOnly` is; `Destructive` and `Idempotent` are 51 hand-written booleans nothing
reads.
*Mechanism:* copy `ToolSurfaceTests.ReadOnly` twice. The pattern is already the right one -- a name
only appears by somebody adding it and writing why -- and extending it costs two lists and two
assertions.

**8. A result from a degraded workspace says so, without any tool remembering to.**
*Rule today:* the caller is expected to have called `rose_workspace_status` first.
*Mechanism:* `WorkspaceManager.Attribute<T>` (`WorkspaceManager.cs:188`) is already the one place
every result is stamped with its workspace and revision, precisely so "a tool added later cannot
forget it". Stamp the degraded notice there too. The rule and the mechanism already exist; only one
more fact needs to travel through them.

## Open questions for Steve

1. **Is 51 the right number, and is the live-app half paying its way?** Thirty Roslyn tools do the
   work the product is named for; 21 debug and XAML tools cost roughly the same context and are
   Windows-only. Has an agentic session actually driven the debug surface end to end, or is the
   Inspector the real consumer? If it is the Inspector, some of those 21 could move to the operator
   API and off the model's context entirely -- `rose_live_app_frames` already made that choice.
2. ~~**What is the intended base for a relative path?**~~ **Answered, #305:** the calling
   session's directory, and the argument help says so.
3. **Does `rose_format` intend to be the check?** Its description says "pass apply=false to check
   formatting without writing", and #218 shows it passing a file `dotnet format` fails. Is the
   contract "what IDE0055 thinks" or "what CI will think"? They are different tools.
4. ~~**Is there a reason `rose_find_implementations` has no `project`?**~~ **Answered, #383:** it
   was an omission, and it takes one.
5. **Should the writing tools be usable without the model having read the file?** Today
   `rose_replace_body`'s `find` and `rose_replace_member`'s whole-declaration payload both assume the
   caller knows what is currently there. An agent that has not read the file cannot use either, and
   `rose_symbol_info includeSource=true` is the intended answer -- but nothing on the write tools
   says so. Is that worth a sentence in the write descriptions?

## Rose dogfooding notes

Read-only brief, so no write tool was exercised; everything below is a read. The workspace was
already loaded (revision 1) and reported `Degraded` for the reasons the brief's ground truth lists.

| Tool | For | Outcome |
|---|---|---|
| `rose_outline` `WorkspaceManager`, compact | "What does this class contain", cheaply | ~~**Lost** to a grep of the same question, at many times its size (AGT-01).~~ **#374.** A member is its name, kind, accessibility and line. |
| `rose_outline` `WorkspaceManager`, full | The same, with signatures and docs | Worked. Genuinely more informative than grep -- base types, `isGenerated`, accessibility -- ~~but not worth its size twice in a session (AGT-02).~~ **#374.** Documentation is a sentence per member. |
| `rose_symbol_info` `Microsoft.CodeAnalysis.Workspace.CurrentSolution` | Reproduce the metadata failure another reviewer hit | **Worked**, which is the interesting part: it disproved "metadata is broken" and led to the real rule (AGT-03). |
| `rose_symbol_info` `ModelContextProtocol.Server.McpServer.SessionId` | The reviewer's actual case | **Failed**, with five unrelated source symbols offered as candidates. |
| `rose_symbol_info` `System.Collections.Generic.List` | Wrong arity on a metadata type | **Failed**, offering four methods named `List` in Rose's own source. No mention of arity, none of metadata. |
| `rose_symbol_info` `ModelContextProtocol.Server.McpServerTool` | What the SDK says about tool errors | Worked, and was the authority for the `isError`-versus-thrown question in this review -- ~~but as raw XML (AGT-11).~~ **#374.** The summary is prose. Rose answered a question about its own dependency that I had no other way to ask, which is a real win despite the size. |
| `rose_symbol_info` `RoseMcp.Broker.WorkspaceManagr.CallAsync` (typo) | Grade the near-miss error | **Excellent.** The fix was in the message. |
| `rose_find_references` `ToolNames.WorkspaceStatus` | 16 call sites by containing member | Worked. Beat grep on precision: grep for `WorkspaceStatus` also matches `WorkspaceStatusReport`, `WorkspaceStatusReporter` and the tool name in strings. ~~Lost on shape (AGT-06).~~ **#378, #374.** Listed by file, each path relative to the caller. |
| `rose_find_references` same, `definitionsOnly=true` | Just the count | Worked, ~~but `truncated: true` over an empty list (AGT-05).~~ **#378.** |
| `rose_search_symbols` `ToolErrorReporting` | Find the three copies | **Excellent, and beat grep outright.** Three addresses ready to paste into the next call; `find -name` would have given me paths and no addresses. |
| `rose_resolve_name` `ToolErrorReporting` (no filePath) | Ambiguous short name | ~~**Failed** with a leaked Roslyn parameter name (AGT-04).~~ **#306, #431.** |
| `rose_resolve_name` `ToolErrorReporting` + filePath | The same question, scoped | **Excellent.** "in scope already, so the error is something else: a misspelling, an accessibility problem, or the wrong number of type arguments" is the best single sentence on the surface. |
| `rose_resolve_name` `Encoding` (no filePath) | Control, to isolate the failure above | Worked. So the failure is the argument shape, not the tool. |
| `rose_diagnostics` one file, then the solution | "Does it compile" | **Worked, and is the strongest thing in the product.** Whole solution, 18 projects, a few seconds, against a `dotnet build` of 30-60 s. Every agentic session pays that difference dozens of times. |
| `rose_outline workspace=C:\Windows\System32` | Grade a bad-path error | **Excellent**, names the problem and the three extensions that would fix it. |

Reached for grep instead, and why: the descriptions and tests are *prose*, so every question about
them ("which tools are in the ReadOnly list", "how many `McpServerTool` attributes", "does anything
set `Destructive`") is a text search and legitimately grep. Reading whole files was deliberate -- a
review of a tool surface has to read the surface. The one place I should have used Rose and did not:
counting the tools. I grepped `McpServerTool` per file; `rose_find_references` on
`ToolNames.LiveAppPairs` or an outline of `ToolSurfaceTests` would have been the same effort and
would have caught that the Roslyn list is 30 and not 31.

Not reachable at all, and worth noting as an absence: there is no way to ask "what does this tool's
schema look like as a client sees it" from inside Rose. `ToolBudgetTests` and `ToolSurfaceTests`
both build a `ServiceCollection` to find out, which means the only way to inspect Rose's own surface
is to run a test. For a product whose thesis is that the surface is the feature, a
`rose_worker_info`-style introspection of the listing -- or simply a committed snapshot file the
budget test diffs against -- would make every change to it reviewable in the diff rather than only
in a pass/fail.
