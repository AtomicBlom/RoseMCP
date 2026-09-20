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
| T4a | `rose_resolve_name name=ToolErrorReporting` (no `filePath`) | error | -- | **failed**: `Parameter 'symbol' must be a symbol from this compilation or some referenced assembly. (Parameter 'symbol')` -- #121 reproduced, on a tool that has no `symbol` argument |
| T4b | `rose_resolve_name name=ToolErrorReporting filePath=src/RoseMcp.Broker/WorkspaceManager.cs` | 1 candidate + "in scope already, so the error is something else" | 700 B | **excellent** |
| T5 | `rose_find_references symbol=RoseMcp.Contracts.ToolNames.WorkspaceStatus includePreviews=false` | 16 hits, `truncated:false` | 4.1 KB | worked; flat list, absolute path repeated 17 times |
| T6 | same, `definitionsOnly=true` | `references: []`, `totalCount:16`, **`truncated:true`** | 600 B | misleading -- see AGT-05 |
| T7 | `rose_search_symbols query=ToolErrorReporting` | 3 matches, each with an `address` | 1.6 KB | **excellent**; the address is the next call's argument |
| T8 | `rose_symbol_info symbol=RoseMcp.Broker.WorkspaceManagr.CallAsync` (typo) | error naming both real `CallAsync` declarations | -- | **excellent**; the fix is in the message |
| T9 | `rose_outline symbol=... workspace=C:\Windows\System32` | "No solution or project found at or above 'C:\Windows\System32'. Pass the path to a .sln, .slnx, or .csproj." | -- | **excellent** |
| T10a | `rose_diagnostics filePath=src/RoseMcp.Broker/WorkspaceManager.cs` | 0 diagnostics | 240 B | sub-second |
| T10b | `rose_diagnostics` (whole solution, 18 projects) | 0 diagnostics | 240 B | a few seconds against a `dotnet build` of 30-60 s. **The single strongest win on the surface.** |

## Findings

### AGT-01 `rose_outline`'s compact mode is not compact, so the tool loses to `Read` on exactly the files it exists for
- **Severity:** High
- **Measured, #295**, at 512 bytes per member with both size controls off. The card lowers it.
- **Effort:** S
- **Where:** transcript T1a/T1b/T1c; `src/RoseMcp.Worker/OutlineService.cs:192`; `src/RoseMcp.Contracts/ToolDescriptions.cs:143-145`
- **What:** With `includeSignatures=false` and `includeDocumentation=false` -- the tool's two documented size controls, both off -- a 441-line class with 24 members costs ~10.1 KB. Full mode costs ~22.3 KB. `grep -n "public\|internal"` on the same file costs 748 bytes and answered the question I actually had. The reason is `OutlinedMember.Location`, emitted unconditionally, carrying the 95-character absolute file path, the whole source line as `preview`, `containingMember` (which for a declaration is always the member's own name), `project` and `isTestProject` -- roughly 350 bytes per member of which about 12 are the answer. Worse, `preview` *is* the signature for most members, so `includeSignatures=false` removes a duplicate rather than the content. Two other reviewers hit this independently; on `CorDebugSession` it produced 70,649 characters, blew the client's token cap, and the reviewer read the file instead (issue #234).
- **Why it matters:** This is the tool whose own description says "Use it instead of reading the file to find out what is in it, which is the read that comes before most edits". An agent that pays 22 KB once and learns nothing it could not have grepped will not pay it twice, and the dogfooding rule says a tool nobody reaches for is a bug of the same severity as one returning wrong answers. It is also self-defeating: the bigger the type, the more the outline is worth and the less usable it is.
- **Suggested change:** Make the location cost proportional to what was asked for. Emit `line` alone when `includeSignatures=false` (the file is already named once on the enclosing `OutlinedType.declarations`); drop `preview`, `containingMember`, `project` and `isTestProject` from member locations entirely, since all four are constant across the answer or derivable from it. Add the two narrowings #234 asks for -- a `members` name filter and `maxResults`/`offset` with a total -- so a 110-member type can be asked a question rather than dumped. Target: a 24-member compact outline under 1.5 KB.

### AGT-02 `includeDocumentation` returns the whole summary, where the schema promises "the first line"
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Worker/OutlineService.cs:240-260`; `src/RoseMcp.Contracts/ToolDescriptions.cs:140-141`; `src/RoseMcp.Contracts/OutlineResult.cs:39` and `:75`
- **What:** Three statements of one contract, all different. The argument help says "the first line of its documentation"; the DTO says "The first sentence of its documentation"; `OutlineService.Summary` returns the entire `<summary>` element, every `<para>` included, flattened to one line. In T1b that made `WorkspaceManager.WorkspaceFor`'s entry ~1,500 characters -- one member costing twice the whole grep -- and the primary constructor's entry repeated the class summary verbatim a second time in the same payload.
- **Why it matters:** An agent budgets from the schema. It is told documentation costs one line per member, it costs a paragraph, and `includeDocumentation=true` is the **default** -- so the setting most likely to overflow a context window is the one chosen by a caller who was told it was cheap. It is unbounded on a third-party type.
- **Suggested change:** Return the first sentence, as the DTO says -- cut at the first `. ` outside a tag -- and add a `summaryLength` cap. Then make `IncludeDocumentationArgument`, `OutlinedType.Summary` and `OutlinedMember.Summary` quote one sentence of the same text, and assert in `ToolDescriptionTests` that a member's summary in an outline is shorter than the same member's in `rose_symbol_info`.

### AGT-03 A metadata symbol is unreachable whenever any source symbol anywhere shares its *leaf* name
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Worker/SymbolTarget.cs:65-76`; `src/RoseMcp.Worker/DeclarationLocator.cs:237-271`; transcripts T2, T3a, T3b
- **What:** `SymbolTarget.ResolveAsync` falls back to `MetadataSymbols.FindAsync` only when the source search throws `SymbolNotFoundException`, which `DeclarationLocator.NotFound` raises only on its `named.Count == 0` arm (`:237-241`). Every other arm throws a plain `ArgumentException`, and the fallback never runs. So the *leaf* name decides: `Microsoft.CodeAnalysis.Workspace.CurrentSolution` resolves because nothing in this solution is called `CurrentSolution`; `System.Collections.Generic.List` does not, because `SolutionFileReader.List` exists; `ModelContextProtocol.Server.McpServer.SessionId` does not, because five source records have a `SessionId`. The refusal then lists those five, which are in unrelated namespaces and are candidates for nothing. The caller wrote a fully qualified name that shares nothing with any of them but its last word.
- **Why it matters:** `rose_symbol_info` promises it answers "about a type in a referenced assembly", and `rose_find_implementations` promises `"what here implements IDisposable" is one call`. Whether that promise holds is decided by a collision the caller cannot see and did not cause, and it fails *more* often the larger the solution -- `List`, `Name`, `Path`, `Value`, `Id` are the leaf names most worth asking about and the likeliest to collide. The reasoning recorded on `SymbolNotFoundException` ("An ambiguous match ... is never a reason to look in metadata") is right for a *bare* name and is being applied to a *qualified* one, which is a different question.
- **Suggested change:** Take the metadata fallback whenever `matching.Count == 0` and the requested path is qualified -- that is, when nothing in source is declared *at the address the caller wrote*, rather than when nothing in source carries the last word of it. Keep the existing refusal for a bare leaf name, which is the ambiguity the exception was written for. Then have "Nothing is declared at" say whether a metadata search ran, so a caller can tell "not in your source" from "not anywhere".

### AGT-04 A leaked Roslyn exception names an argument the tool does not have
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Worker/NameResolver.cs:197`; transcript T4a; issues #121, #212
- **What:** `rose_resolve_name name=ToolErrorReporting`, with no `filePath`, returns
  `Parameter 'symbol' must be a symbol from this compilation or some referenced assembly. (Parameter 'symbol')`.
  `rose_resolve_name` declares `name`, `filePath`, `arity`, `maxResults` and `workspace`. There is no `symbol`. The throw is `compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly)` at `NameResolver.cs:197`, asked of a compilation the candidate did not come from -- filed as #121 in this repository and still open. It is the same sentence #212 reports out of `rose_replace_member`, where it means "a name in your *code* did not resolve" and names the one argument that was correct. With a `filePath` the same call succeeds and gives a genuinely excellent answer (T4b), so the failure is in the shape of the call, not in the question.
- **Why it matters:** This is the worst error on the surface and it is on the tool whose entire job is unsticking a caller who is already stuck. Every honest reading of it is wrong and expensive -- re-derive the address, reload the workspace, check the project -- and #212 records a retry spent on each. It also breaks the assembly's own stated rule ("Every other refusal on this surface says what was wrong with what the caller sent and what to send instead", `ToolArgumentShape.cs:11-13`) in the one place a caller has no other move.
- **Suggested change:** Fix the provenance at `NameResolver.cs:197` -- ask the compilation the symbol came from, or map the candidate in with `SymbolFinder.FindSimilarSymbols` before testing accessibility. Then add the general guard at the boundary: **no message naming a CLR parameter may reach a caller**, because the caller's vocabulary is the tool's schema. A filter that rewrites any message containing `(Parameter '` into one naming the tool's own arguments, plus a test over all three `ToolErrorReporting` copies, closes the class rather than this instance.

### AGT-05 `definitionsOnly=true` reports `truncated: true` over an empty list
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Worker/NavigationService.cs:127-129` and `:139`; transcript T6
- **What:** With `definitionsOnly=true` the code sets `listed = []` and then `Truncated = listed.Length < ordered.Length`, so every such call reports `truncated: true` with `references: []` and the real `totalCount`. The comment defends it: "Truncated says the list is not all of them, which is as true of asking for none as of asking for two hundred."
- **Why it matters:** From the caller's chair `truncated` means exactly one thing -- *raise `maxResults` and call again*. Here that call returns the identical result forever. Two reviewers in this round hit it independently and both recorded it as misleading, which is the signal that the field's meaning in the code is not its meaning on the wire.
- **Suggested change:** `Truncated = !definitionsOnly && listed.Length < ordered.Length`. The suppression is already evident to the caller, who asked for it. If a distinct signal is wanted, add `listSuppressed: true` rather than overloading the retry flag.

### AGT-06 `rose_find_references` promises grouping and returns a flat list with the absolute path repeated per hit
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/SourceLocation.cs:18-22`; `src/RoseMcp.Contracts/ToolDescriptions.cs:577-578`; transcript T5
- **What:** The description says "Each hit names the member it sits inside, which turns a flat list into 'used by these six methods'", and `SourceLocation.ContainingMember` says the same. The result is a flat array; the grouping is a thing the *agent* must do. Each of the 16 entries carried the same 95-character absolute path prefix, plus `project` and `isTestProject` -- with previews off, 4.1 KB of which roughly 1.6 KB is the workspace root written out 17 times, under a `workspace` field that already names it once.
- **Why it matters:** The `01-broker-and-server` reviewer got 53 hits on one symbol and called the answer noisy; at the 200-hit default that is ~20 KB of repeated path. The tool's claim over grep is precision *and* answering in the unit the caller thinks in (methods). It delivers the first and asks the caller to compute the second, at a size where the caller may not have room to.
- **Suggested change:** Emit paths relative to the `workspace` root already in the result (absolute only when outside it), and lift `project`/`isTestProject` to a per-file header. Then offer the grouping the description sells: `groupBy: "member" | "file" | "none"`, defaulting to `member`, which collapses the common answer to a name and a count per member.

### AGT-07 `rose_find_implementations` has no `project` filter, so the question it advertises is the one it cannot answer
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Broker/Tools/BrokerAnalysisTools.cs:276-283`; `src/RoseMcp.Contracts/ToolDescriptions.cs:588-589`
- **What:** The description's worked example is `"what here implements IDisposable" is one call`. The tool takes `maxResults` and no `project`, where its sibling `rose_find_references` takes both. The `03-liveapp` reviewer ran exactly the advertised call, got 1,312 matches truncated twice, every one from `ClrDebug`, `WinRT.Runtime`, ASP.NET and Roslyn metadata, could not scope it (passing `workspace` does not narrow), and fell back to grep.
- **Why it matters:** For a BCL or framework interface -- the case the description chose to advertise -- scoping is not an optimisation, it is the only form of the question. Truncating at 200 without it returns 200 arbitrary matches from dependencies and calls itself an answer.
- **Suggested change:** Add `project` with the same semantics and the same refusal-on-unknown-name as `rose_find_references` (`NavigationService.cs:143`), and add `sourceOnly` (default true) so metadata implementations are excluded unless asked for. Assert in `ToolParityTests` that the two navigation tools offer the same narrowing arguments.

### AGT-08 A misspelled argument is dropped in silence and the error then reports the value as missing
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/ToolArgumentShape.cs:51`; issue #249
- **What:** `ToolArgumentShape.Mismatch` skips any argument the schema does not declare (`if (!properties.TryGetProperty(name, out var declared)) continue;`), and it only runs after the binder has refused -- which an unknown argument never causes, since it binds fine with the declared arguments at their defaults. So `rose_outline(file: "...")` answers "Name a type, as `Namespace.Type`, or give a file path", which is the one thing the caller did. #249 records it costing two round trips and a read of `BrokerAnalysisTools.cs` to learn the spelling.
- **Why it matters:** An argument name is part of a tool's vocabulary, and this surface has several near-misses an agent will plausibly guess: `file`/`filePath`; `type`/`symbol` (the help for `symbol` on `rose_outline` literally reads "The type, as Namespace.Type"); `name`/`symbol`; and three words for imports -- `usings` on the member writers, `namespaces` on `rose_add_using`, `extraUsings` on `rose_add_file`. A wrong guess produces an error pointing at the wrong bug, and a session that has to read Rose's source to call Rose has already lost to grep.
- **Suggested change:** #249's fix exactly. Collect the undeclared names instead of skipping them, and run the helper on any refusal naming a missing argument: *"`file` is not an argument of rose_outline -- did you mean `filePath`?"*, matched by edit distance against the declared set. Then close the near-misses: accept `type` as an alias on `rose_outline`, and make one word mean imports everywhere.

### AGT-09 Two conventions for an enum-like argument, and the better one is used on three tools
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs:42` (`InspectorVisibility showInspector`) versus `BrokerAnalysisTools.cs:26,27,325,442` and `LiveAppDebugTools.cs:434` (`string? scope`, `string? minimumSeverity`, `string scope`, `string? position`, `string mode`)
- **What:** `showInspector` is a real C# enum, so the SDK emits a JSON Schema `enum` and the client rejects a wrong value before it reaches the server. `scope`, `minimumSeverity`, `position`, `callSites`, `action`, `verifyScope`, `mode` and `kinds` are strings whose valid set lives only in the prose and in `ArgumentValues`' runtime refusal. Confirmed in the schema as sent: `"scope": {"type": ["string","null"], "description": "document, project, or solution. Defaults to solution."}` -- no `enum` key.
- **Why it matters:** `ArgumentValues` exists because four of these silently defaulted a typo into a confident answer to a different question, and it fixed that at runtime -- one wasted round trip per typo. The enum form costs zero round trips, because the constraint is in the schema the model reads while composing the call. The surface already knows how, and uses it on the three least important arguments.
- **Suggested change:** Make every fixed-set argument a C# enum, keeping `ArgumentValues` as the worker's own parse (it is driven standalone and must still refuse). Where a synonym is accepted -- `file` for `document` -- make it an enum member. Add a `ToolSurfaceTests` assertion: no advertised argument whose description names alternatives may lack an `enum` in its schema.

### AGT-10 A relative path resolves against the server process's working directory, which on a six-worktree machine is a write to the wrong checkout
- **Severity:** High
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/WorkspaceHints.cs:28-36`; `src/RoseMcp.Broker/ServiceCollectionExtensions.cs:181-187` (`OriginDirectory`); `src/RoseMcp.Contracts/ToolDescriptions.cs:104`; issue #214
- **What:** Every path argument is documented "Absolute or **solution-relative**", but which solution is what the path is being used to *decide*, so the base is in fact the broker process's working directory unless the client sent `_meta` with a `CallOrigin` directory -- and only a relay does (`OriginDirectory` returns null otherwise). `WorkspaceHints`' own doc says the hazard out loud: "Resolving 'Db.App' as a path makes it relative to the process working directory, which for the tray is its own install directory and for anyone else is a directory that has nothing to do with the question". #214 is this landing as a **write into a different checkout, reported as success**: `rose_add_using` with `tests/RoseMcp.IntegrationTests/LiveAppSessionTests.cs` edited the file in `C:\Dev\Personal\RoseMCP` while every other call in the session answered from the worktree. This machine currently holds six checkouts of this repository side by side, so it is the ordinary case here rather than a corner.
- **Why it matters:** It is the only failure in the whole corpus undetectable from the working copy the session can see: the result reads as success, `git status` in the worktree is clean, and the change sits in a repository whose own sessions are free to commit it. Every other defect here is a wrong answer; this one is a wrong write.
- **Suggested change:** Stop resolving a relative path against a process-wide default. Resolve against the *session's* origin directory when one is known, and when none is, **refuse a relative path on a write argument** with "give an absolute path, or pass `workspace`" rather than guessing. Make the argument help say what the base actually is. A `ToolParityTests` case asserting that two same-named solutions resolve differently from the same relative path would pin it.

### AGT-11 `rose_symbol_info` returns raw, unbounded XML documentation
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Contracts/SymbolInfoResult.cs:27-28`; transcript T3c
- **What:** `Documentation` is `GetDocumentationCommentXml()` verbatim: the `<member name="T:...">` wrapper, every `<see cref="T:Fully.Qualified.Name"/>`, `<list type="table">` markup, and the SDK's own indentation. Asking what `ModelContextProtocol.Server.McpServerTool` is cost 9.2 KB, of which the answer is about 200 characters and the rest is `cref` attributes. There is no `includeDocumentation=false` and no cap.
- **Why it matters:** `rose_symbol_info` is where the server instructions route "what a symbol is", and the first thing an agent does with an unfamiliar dependency. A well-documented third-party type is where it is most wanted and costs most. Note the asymmetry: `rose_outline` at least parses the XML (AGT-02 says it over-includes); `rose_symbol_info` does not parse at all.
- **Suggested change:** Return parsed sections (`summary`, `remarks`, `returns`, `params`) with `cref`s rendered as short names; add `includeDocumentation` (default true) and `maxDocumentationLength` (default ~800) and report `documentationTruncated` when cut. Share the parse with `OutlineService.Summary` so the two cannot disagree about what a summary is.

### AGT-12 `rose_diagnostics` never says the workspace is degraded, so a clean answer from a broken workspace reads as a clean bill of health
- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Contracts/DiagnosticsResult.cs:23-24`; transcript T10b; `docs/invariants/result-shapes.md`
- **What:** This workspace is `Degraded` -- two source generators fail to load with a manifest version mismatch, and the WinUI projects cannot resolve `RoseMcp.Contracts.dll` during the design-time build. `rose_diagnostics` over the whole solution returned `diagnostics: []`, `totalCount: 0`, `notices: []`. Nothing in the result says the workspace it came from does not trust itself, although `notices` exists and is exactly where it would go.
- **Why it matters:** `rose_diagnostics` is what the instructions route "does it compile" to, and what the dogfooding rule says to use instead of a build. An agent treats `0` as a gate and moves on. A project whose design-time build failed resolves no references and a generator that failed to load produces no code, so `0` from a degraded workspace is not the same fact as `0` from a healthy one -- and `result-shapes.md` is explicit that reporting a signal which cannot mean what it says is the failure to avoid. `MemberEditResult` models this correctly with `Verified` and `DependentsNotChecked`; the read path does not.
- **Suggested change:** When the workspace state is not `Healthy`, put one notice on every `DiagnosticsResult`: which projects are untrustworthy, the one-line fix, ending in "ask rose_workspace_status". Do the same for `OutlineResult.Notices` and `ReferencesResult`. Cheap, since the state is already computed.

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

### AGT-17 Whitespace fidelity is why an agent stops trusting the writer, and `rose_format` certifies the damage as clean
- **Severity:** High
- **Effort:** L
- **Where:** issues #195, #197, #200, #217, #218; `docs/invariants/writing-csharp.md`; `CLAUDE.md`, "Whatever writes C# has to end formatted"
- **What:** Five open issues, one shape. `rose_replace_body`'s `find`/`replace` adds the destination's indentation on top of the replacement's own, producing four-tab lines inside a two-tab block, and `position: end` re-emits the whole body so the diff touches every statement already there (#217). `rose_change_signature` joins a six-line parameter list into one 168-character line (#197). `rose_add_file` writes LF and spaces into a project the workspace loaded after startup (#218), and puts the `usings` argument in a block of its own above the code's own imports, which `dotnet format` rejects (#200). `rose_replace_body` duplicates a comment before the match and silently deletes one between matched tokens (#195). In #218 and #217, `rose_format` then reports "Every file was already formatted", because IDE0055 has no opinion about collection-expression indentation or parameter-list wrapping.
- **Why it matters:** This is the answer to "what would make an agent go back to `Edit`", and it is not any one bug -- it is that the recovery loop does not exist. The documented workflow is *write with a rose tool, check with `rose_format`*, and in these cases both steps report success on a file a reviewer will reject and CI may fail. An agent cannot distinguish a good write from a bad one without reading the file back, and once it is reading the file back, `Edit` is cheaper and its result is already known. Every other property of the writing surface -- named addressing, parse-before-write, compile-after-write -- is worth nothing against a caller who has learned to verify by hand.
- **Suggested change:** Make "what the edit changed" a *measured* property rather than a hoped-for one. Every write tool already produces a unified diff; assert against it before returning. If the diff touches a line the edit did not name, report those lines as `unrequestedChanges` in `notices`, and refuse rather than apply above a threshold. One guard, at the boundary, catching #197, #217 and #195's interior-comment deletion at once, that the next writer cannot forget. Separately, make `rose_format` answer the question it is asked: today it reports what IDE0055 thinks, so either give it `dotnet format --verify-no-changes` semantics or stop its description implying it is the check.

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
- **Why it matters:** This is the class the repository has already been bitten by -- `ToolDescriptions` exists because two copies of the same text drifted. A rule like AGT-04's has to be added in three files, and the third is the one that gets missed.
- **Suggested change:** The decision to keep MCP types out of `Contracts` is right; the split is in the wrong place. Move the pure part to `Contracts` as `ToolFailure.Message(JsonElement inputSchema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, Exception exception, string? suffix)` -- all values, no MCP types, exactly the shape `ToolArgumentShape.Mismatch` already takes -- and leave each host the six-line filter that reads them off its own `RequestContext`. Then the rule has one home and a test.

### AGT-20 Two agents on one stdio broker share every workspace and every debug session, with nothing to tell them apart
- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/CallSession.cs:14-18`; `src/RoseMcp.Broker/LiveAppSessionManager.cs:83-87`, `:156-157`; `src/RoseMcp.Contracts/ToolDescriptions.cs:77-78`
- **What:** Ownership of a live-app session is `CallSession.Id`, the MCP transport's session id, documented as "Null is the honest value for a stdio broker, which has one session for the life of the process, so every call in it owns everything it started". That is true of the *transport* and false of the *agent*: Claude Code runs subagents over one MCP connection, so N subagents share one null owner. `Find` therefore matches, and a subagent can `rose_debug_list` a sibling's session, set breakpoints in its target, evaluate inside it, or `rose_debug_detach` it. The same holds for `rose_workspace_reload` (a full design-time build, ~21 s here, invalidating every warm answer another agent holds) and `rose_workspace_close`. On the read side `revision` is per workspace and global, so two agents editing one solution see each other's revisions with no way to tell whose. `expectedRevision` is the right primitive, but its help -- "Fail rather than apply if the workspace has moved past this revision" -- never says it is what you use when something else might be writing, and the instructions never mention concurrency at all.
- **Why it matters:** Several subagents in one repository is the ordinary way this repository is worked on -- this review is four agents in one worktree -- and the only concurrency story is a transport-level notion of session that the agent layer does not correspond to. The failure is quiet: the sibling's next `rose_debug_continue` fails with a session id that no longer exists, which reads as a bug in the debugger.
- **Suggested change:** Short term, say it in the writing: make `ExpectedRevisionArgument` name the case ("pass the revision your last read returned; another agent or a human editor may have written since"), and have `rose_debug_list` mark which sessions *this call* started rather than only which it may reach. Medium term, give a call an agent identity independent of the transport -- the `_meta` channel that already carries `CallOrigin` can carry one -- and default the destructive lifecycle tools (`rose_workspace_reload`, `rose_workspace_close`, `rose_debug_detach`) to refusing a target they did not start, with an explicit `force`.

### AGT-21 A write result is roughly 4,000 characters, of which about 85% is the caller's own input, a constant, or a fact already stated

- **Severity:** High
- **Measured, #295**, at 1,895 bytes for an edit that introduces no diagnostic -- the floor, rather than the 4,000 this finding measured for one that did.
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/MemberEditResult.cs:38` (`Diff`),
  `src/RoseMcp.Contracts/WorkspaceMutationResult.cs:17,23` (`ChangedFiles`, `Notices`),
  `src/RoseMcp.Worker/MemberSyntax.cs:196` (line-ending notice),
  `src/RoseMcp.Worker/EditVerification.cs:129-130` (analyzer notice),
  `src/RoseMcp.Worker/DeclarationEditService.cs:201-202` (dependents notice, via
  `EditVerification.SkippedDependents`), `src/RoseMcp.Contracts/DiagnosticEntry.cs:32` (`HelpLink`)
- **Cheaper than when filed.** Card 9 shipped, so the notices this finding wants trimmed are decided in
  `EditPipeline.Report()` rather than in six hand-written iterators. Card 9's own rule applies to the
  trim: a line saying *which* compile ran is a fact and stays; a line framing the compile is shared and
  can be conditioned in one place.
- **What:** Measured on one real `rose_replace_member` response that added a doc comment and one
  statement, and came back with one error. Roughly 4,000 characters, about 1,000 tokens. It breaks
  down as:

  | Part | Size | Verdict |
  |---|---|---|
  | `diff` | ~2,100 | Almost entirely the doc comment the caller had just sent |
  | Five `notices` | 1,028 | Two unconditional, one restates the diagnostic, one contradicts a field |
  | Scaffold (16 fields) | 590 | The absolute path appears five times |
  | One `introducedDiagnostics` entry | ~350 | Includes a `helpLink` no agent fetches |

  Eight distinct redundancies, in increasing order of how structural they are:

  1. **The absolute path five times** -- `filePath`, both diff headers, `changedFiles[0]`, and
     `introducedDiagnostics[0].filePath`. About 300 characters to say one thing.
  2. **`members: ["RunProcess"]`** restates the tail of `symbol`, which is in the same object.
  3. **`totalErrorCount: 1`** is indistinguishable here from `introducedDiagnostics.Length`, and
     nothing says whether it counts errors that were already there.
  4. **`helpLink`** on a CS0103. No agent has ever opened one.
  5. **The diff echoes the caller's own input.** The agent composed that doc comment; reading it
     back teaches nothing. The only facts the callee owns are *where it landed* and *what was
     normalised*, and both are one line each.
  6. **One fact stated three times.** The diagnostic message says the name does not exist; notice 2
     says "MSBuildEnvironment resolves to nothing in scope, and no import would fix it"; notice 4
     opens by saying it again before giving the advice. Notice 2 is pure restatement.
  7. **Two notices are constants.** The line-ending notice fires whenever the supplied code used LF,
     and its own text concedes that is "what composing C# for a tool argument produces without
     anyone deciding to" -- so it fires on substantially every write, at 394 characters. The
     analyzer notice is emitted on *both* branches of the ternary at `EditVerification.cs:129-130`,
     so it fires always. **A notice that fires on nearly every call carries no information; it is
     documentation, and belongs in the tool description.**
  8. **One notice contradicts a field in the same payload.** The dependents notice is guarded on the
     edit *kind* (`request.Kind == MemberEditKind.Replace`) and not on whether any dependent exists,
     so it says "Only X was compiled ... which this did not check" while `dependentsNotChecked: []`
     in the same object says there was nothing to check. The comment above it reads "Said only where
     it can happen", which is true of the kind and not of the instance.
- **Why it matters:** This is finding AGT-01's problem on the *write* surface, where it is worse.
  The agent pays a thousand tokens per edit, and an edit loop is many edits. What it actually needed
  from this response is four facts: it applied, it landed at line 174, 34 endings were normalised,
  and one error was introduced with its message and the advice for fixing it. Everything else is
  either something the agent sent, something that is always true, or the same sentence at a
  different length. And the two genuinely valuable notices -- the advice about resolving a name, and
  the warning about unchecked dependents -- are the ones buried at positions four and five behind
  three that are not.
- **Suggested change:** Four rules, applied in one place.
  1. **Never echo the caller's input.** Drop `diff` from the default response; report `at: "174-200"`
     and `normalised: "34 line endings to CRLF"`. Put the diff behind `includeDiff`, default off, and
     make the flag genuinely remove it (cf. AGT-01, where `includeSignatures=false` does not).
  2. **A constant is not a notice.** Emit the line-ending and analyzer notices only when the outcome
     was not the usual one. Move their standing explanation into `ToolDescriptions`.
  3. **Say a fact once, at its most actionable.** Where a notice restates a diagnostic, keep the
     advice and drop the restatement. The advice is the part no other field carries.
  4. **Name a path once.** One `file` field, relative to the workspace root; diagnostics refer to it
     by index, and `changedFiles` lists only the *other* files an edit touched.

  Condition the dependents notice on `SkippedDependents` being non-empty, which is the field that
  already knows.

  Target shape, same information an agent can act on, about 600 characters:

  ```json
  {
    "revision": 1, "applied": true, "verified": true,
    "file": "tests/RoseMcp.IntegrationTests/TestToolchain.cs",
    "symbol": "TestToolchain.RunProcess(string, string)",
    "at": "174-200",
    "normalised": "34 line endings to CRLF",
    "errors": [{ "id": "CS0103", "line": 183, "col": 29,
                 "message": "The name 'MSBuildEnvironment' does not exist in the current context" }],
    "advice": "Nothing of that name is reachable here, so it is not written yet rather than unimported. rose_resolve_name finds one that exists; the usings argument imports it in the same call.",
    "scope": "compiled RoseMcp.IntegrationTests; it has no dependents"
  }
  ```
- **Blast radius, and the root cause.** `WorkspaceMutationResult` is the base of eight result records
  (`AddFileResult`, `CodeFixResult`, `FormatResult`, `MemberEditResult`, `MoveTypeResult`,
  `RenameResult`, `SignatureChangeResult`, `UsingResult`), so `ChangedFiles` and `Notices` are on
  every one of the thirteen writing tools. The same shapes recur on the read surface: a path per
  member in `rose_outline` (AGT-01), a path per hit and a definition listed three to four times in
  `rose_find_references` (AGT-06), unbounded raw XML in `rose_symbol_info` (AGT-11), and a
  `helpLink` plus an absolute path on every entry of `rose_diagnostics`, which at solution scope is
  the worst case in the product.

  The root is **WRK-01**. Eight files under `src/RoseMcp.Worker/` declare their own
  `IEnumerable<string> Notices` iterator, so there is no single place where "is this worth saying,
  and is it already said" gets decided. That is why two notices are unconditional and one disagrees
  with a field beside it. Fixing WRK-01 gives notice discipline somewhere to live, which is the
  same argument `WorkspaceManager.Attribute<T>` already won for attribution.

- **The anchor already exists and is refused as input.** Every result carries `workspace` *and*
  `workspaceKey` (`WorkspaceManager.cs:192`), and `WorkspaceKey`'s own summary says it is "a short,
  stable name for one loaded solution, **fit for a caller to quote back**", derived from the path
  rather than minted per process so it survives a worker restart, and hashed because "six worktrees
  of one repository is the ordinary case, not a corner one". It is written on every result and
  **read as input nowhere** -- the same shape as `HostVersion` (IPC-02) and `InfoAge` (USE-03): a
  fact computed for a consumer that never consumes it.

  This matters for the path question. A relative path is ambiguous only when it arrives with no
  anchor, and an anchor that costs sixteen characters will actually be carried where a sixty-
  character absolute path will not. Accept `workspaceKey` wherever `workspace` is accepted, return
  paths relative to the workspace, and the round trip is unambiguous by construction: the agent
  quotes back the pair it was handed, and no resolution against a process working directory happens
  at all.
- **Order matters: this card is gated on BRK-01.** Returning relative paths makes an agent send
  relative paths -- results are where agents get their arguments -- so shipping the size fix before
  the resolution fix converts a latent hazard into a routine one. Today a relative hint is resolved
  against the *broker's* working directory, which for a tray is its install directory.
  `WorkspaceManager.cs:345-351` already states the failure in a comment -- "Resolving that as a path
  makes it relative to the process working directory and answers from whichever solution is sitting
  there, which is worse than not trying" -- and guards it with `File.Exists`. That guard catches the
  harmless case, a hint that is not a path at all, and **passes the harmful one**: a relative path
  that does exist under the broker's directory binds to the wrong checkout, which is #214.
- **Not everything can be workspace-relative, and the rule should say so.** Anchor absolute and
  stated once; anything under it relative; anything outside it absolute. The live-app surface is
  genuinely outside: module paths read from the debugged process, `InstallLocation`
  (`LiveAppInfo.cs:32`, under `WindowsApps` for a packaged app), `HostLogPath` (`:74`, under
  `LOCALAPPDATA`). A project referenced from outside the solution directory is relative but ascends.
  Generated documents and metadata symbols have no disk path at all.
- **The pit-of-success form: refuse, do not guess.** A relative path arriving with no anchor -- no
  `workspace`, no `workspaceKey`, no origin -- should be refused naming both candidates, not
  resolved against whatever directory the process happens to occupy. That turns #214 from a silent
  write into the wrong worktree into a loud error, and it is the precondition that makes returning
  relative paths safe rather than merely cheaper.

### AGT-22 Tools that are plural by intent are singular by signature, and the cost is model turns rather than round trips

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.LiveApp/Tools/LiveAppTracepointTools.cs`,
  `src/RoseMcp.LiveApp/Tools/LiveAppBreakpointTools.cs`,
  `src/RoseMcp.Broker/Tools/LiveAppDebugTools.cs`; contrast
  `src/RoseMcp.Worker/Tools/RefactoringTools.cs:116` (`string[] filePaths`), `:353`
  (`string[] namespaces`), and `src/RoseMcp.Contracts/LiveXamlApplyResult.cs` with
  `LiveXamlEditResult`
- **What:** `rose_debug_add_tracepoint` takes exactly one location, and so do
  `rose_debug_set_breakpoint`, `rose_debug_remove_tracepoint` and `rose_debug_remove_breakpoint`.
  Instrumenting a code path is never one tracepoint: it is entry, exit, the branch you suspect, and
  the loop you do not trust. Six tracepoints is six calls.

  The tool's own description positions it against the alternative: "Prefer this over adding logging
  statements and rebuilding". **That alternative is plural in a single edit.** A person adding log
  statements adds five in one pass and runs once. The tool it is meant to beat collapses the set into
  one action, and this one does not.

  The same shape appears on the read side, and it has already cost a reviewer. The UI usability
  review wanted "which members of this type are referenced by nobody" across about twenty
  properties, found `rose_find_references` to be one symbol per call, and went to grep. That is a
  documented loss caused by a signature rather than by an answer.
- **Why it matters:** **In an agentic loop a round trip is not a network hop, it is a model turn.**
  Six tracepoints is six turns: six chances for the agent to lose the thread, six result envelopes
  each carrying `revision`, `workspace`, `workspaceKey` and `sessionId` (AGT-21), and six
  opportunities for a partial failure the agent must now reconcile by hand -- three tracepoints set,
  one refused, and no statement anywhere of what the session currently holds.

  This is worth saying because it cannot be fixed at the protocol. Even where JSON-RPC offers
  batching, the model still has to *decide* each call separately, so wire-level batching would save
  nothing that matters here. Only a plural argument shape collapses N decisions into one, which is
  why this is an agentic-citizenship finding and not an API-ergonomics one.
- **Suggested change:** Apply the pattern the repository already has, rather than inventing one.

  1. **Take an array.** Four writing tools already take `string[]` arguments (`filePaths`,
     `namespaces`, `usings`, `arguments`), so there is neither a technical nor a stylistic objection.
     Make `location` a list rather than adding a second plural spelling beside a singular one: two
     spellings of one idea is the inconsistency AGT-09 already raises.
  2. **Return per-item outcomes, copying `LiveXamlApplyResult` exactly.** It already has the right
     shape -- an `Applied` count, a `Total`, one `Results` entry per item with a `Status` that is
     either applied or the reason it was not, `Notes` for what could not be done at all, and
     `Detail` for the case where the whole operation could not run. A tracepoint batch wants
     precisely that: bound, not bound because the module is not loaded yet, refused because the
     condition does not parse.
  3. **Never fail the batch for one item.** The XAML apply already establishes that and the
     reasoning is the same: a partial result the caller can read beats an all-or-nothing refusal
     when the items are independent.
  4. **Sequence it behind the read-size work.** Batching a read whose per-item payload is already
     large multiplies the payload as well as saving the turns: ten outlines at fourteen kilobytes is
     a worse answer, not a better one. So batch the debug family now, where results are small, and
     the read family (`rose_symbol_info`, `rose_outline`, `rose_find_references`) after card 11.

  The tools worth the change, by whether one intent commonly produces many calls: the four debug
  bookkeeping tools now; `rose_find_references`, `rose_symbol_info` and `rose_outline` after the size
  work; `rose_delete_member` and `rose_add_member` as candidates. Genuinely singular and to be left
  alone: `rose_replace_body`, `rose_rename_symbol`, `rose_add_file`, `rose_move_type_to_file`, and
  every execution-control verb, where ordering is the meaning.

### AGT-23 Overflow should return a smaller answer to a better question, never the same answer somewhere else

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Worker/Tools/NavigationTools.cs:80-90` (the filters the tool accepts),
  `src/RoseMcp.Contracts/SourceLocation.cs:12-31` (the facets every hit carries),
  `src/RoseMcp.Contracts/ReferencesResult.cs:21-23` (`TotalCount`, `Truncated`)
- **What:** A hot symbol overflows `maxResults`, and the tool answers with the first 200 hits and
  `truncated: true`. The tempting fix is to spill the full list to a file and tell the caller to
  grep it. **That is the wrong remedy, and the right one is already three-quarters built.**

  Every reference returned carries `ContainingMember`, `Project`, `IsTestProject` and
  `GeneratedHintName`, each with a docstring saying why a caller wants it. `ContainingMember`'s says
  it is "what turns a flat list of forty references into 'used by these six methods', **which is the
  question a caller actually had**". `IsTestProject`'s says "a use from a test is a different fact
  from a use in the product". The tool accepts exactly one of those four as a filter (`project`) and
  groups by none of them, although `ToolDescriptions` promises grouping by member (AGT-06).

  So the code already knows the caller's real question, already computes the facts that answer it,
  already writes down why each matters, and then returns a flat list and a truncation flag.
- **Why it matters:** Three reasons the file-and-grep remedy is worse than it looks.

  1. **It concedes the project's own thesis.** `CLAUDE.md` says that if Rose does not beat grep and
     find-and-replace it has little reason to exist, and that a tool which loses to grep is a defect.
     A result that *instructs* the caller to grep is that defect shipped as a feature, and it trains
     the habit the product exists to break.
  2. **Grepping a dump is strictly worse than grepping source.** The caller paid a semantic tool to
     distinguish an override from a comment that happens to contain the name, and then text-matches
     over the answer, discarding exactly what it paid for.
  3. **The size is a symptom of an unasked narrowing question, not of an answer needing storage.**
     Nobody wants 412 references. They want the ones outside tests, or the ones in one project, or
     the six members that do the calling. Storing all 412 answers the question nobody asked, more
     durably.
- **Suggested change:** Three steps, in order, and a fourth only if asked for.

  1. **On overflow, return the shape instead of the list.** Group by the facets already on every
     hit: "412 references -- 380 in test projects, 22 in `RoseMcp.Broker`, 10 in `RoseMcp.Worker`;
     340 of them inside 6 members. Narrow with `project=`, `excludeTests=true`, or
     `containingMember=`." That is about two hundred characters, it is what a person does before
     reading a list, and it teaches the narrowing vocabulary in the one moment the caller is looking
     for it.
  2. **Accept as a filter every facet you return.** `excludeTests`, `excludeGenerated` and
     `containingMember` beside the `project` filter that already exists. The general rule, which is
     worth stating once somewhere permanent: **every facet a result returns is a filter the tool
     owes.** A fact worth computing per item is a fact worth selecting on.
  3. **Make truncation honest first.** `definitionsOnly=true` currently reports `truncated: true`
     over an empty list (AGT-05). An overflow story built on a truncation flag that lies is worse
     than none.
  4. **A file only on request, never as a fallback.** There is a real bulk case -- feeding a
     scripted refactor -- and for it an explicit `outputFile` the *caller* names is right, because
     the caller then owns the path and the cleanup. An automatic spill turns a read tool into one
     that writes to disk without being asked, and this repository has already learned once what
     happens to directories nobody owns: `CLAUDE.md` records the sandbox that "accumulates a copy of
     the provider and a grant to ALL APPLICATION PACKAGES" when it outlives its host.

  The same rule generalises to every list in the product: `rose_diagnostics` at solution scope,
  `rose_search_symbols`, `rose_debug_events`. Overflow is a prompt to ask a better question, and the
  tool is the thing that knows what the better questions are.


## Why tools lose to grep, ranked

From the 18-issue corpus, the three other reviewers' dogfooding notes, and my own ~30 calls. Ranked
by how often it decides a call, not by severity.

1. **The answer is too big to use** (AGT-01, AGT-02, AGT-06, AGT-11, #234). The commonest loss, and
   the only one where the tool *worked*. `rose_outline` at 22 KB, `rose_find_implementations` at
   1,312 matches, `rose_symbol_info` at 9 KB of XML. An agent that cannot afford the answer greps,
   and it does not come back.
2. **The name the caller wrote cannot be addressed** (AGT-03, #210, #233, #239). Positional record
   properties, a type whose namespace ends in its own name, a metadata symbol whose leaf name
   collides, an `out` parameter dropped from the reported symbol. The addressing grammar is the
   product's central claim and it is not total, so the agent learns to keep a position to hand --
   which is the grep it was meant to replace.
3. **The error does not say what to do** (AGT-04, AGT-08, #121, #212, #210, #249). A leaked
   `(Parameter 'symbol')`, a dropped argument name reported as a missing value, advice to make the
   call that just failed. Each costs one to three round trips, and the agent's next move after two
   failed round trips is always the tool it already trusts.
4. **The write is not trusted** (AGT-17, #195, #197, #200, #217, #218). Whitespace, import grouping
   and comment damage that `rose_format` then certifies as clean. Once an agent has to read the file
   back to check a write, `Edit` is strictly cheaper.
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

**1. ~~Compact has to be measured, not intended.~~** **Half done, #295.** The three shapes tier 3
shrinks are held to a ceiling, so the cards that shrink them have a number to move. Splitting the
location record into the two shapes it is used as is the other half, and is card 11's.

**2. No CLR vocabulary reaches a caller.**
*Rule today:* "convert at the MCP boundary, never at the throw site" (`CLAUDE.md`), which converts
the *exception* and leaves whatever text Roslyn put in it.
*Mechanism:* the boundary filter rewrites, rather than forwards, any message containing
`(Parameter '` or a `Microsoft.CodeAnalysis.` type name -- into the tool's own argument names, or
into "an internal error in <tool>; this is a bug, please file it" with the detail in the log. One
test over all three `ToolErrorReporting` copies (which AGT-19 would make one). #121, #198 and #212
are all this class, and nothing today can notice the fourth.

**3. An unknown argument is a caller error the tool can see.**
*Rule today:* nothing; the binder drops it and the tool reports the value as missing.
*Mechanism:* #249's change. `ToolArgumentShape` already enumerates the supplied names against the
schema and already skips the undeclared ones at `ToolArgumentShape.cs:51`; collect them instead, and
run the helper on any refusal that names a missing argument, not only on a binder refusal. The
schema is to hand at all three boundaries. This is the smallest change on this list with the largest
effect on a first-time caller.

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

**6. A relative path cannot be resolved without a base.**
*Rule today:* the caller remembers to pass absolute paths when several checkouts exist; the docs say
"solution-relative" and the code resolves against the process's working directory.
*Mechanism:* make the base a type. A tool argument arrives as a `string`, and the only way to turn
it into something the file system sees is `RepositoryPath.From(raw, origin)`, which takes the
session's origin directory and throws where the path is relative and no origin is known. Then a tool
that forgets does not silently write elsewhere; it does not compile. Mirrors what `WorkspaceHints`
already did for the routing ranking.

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
2. **What is the intended base for a relative path?** The argument help says "solution-relative";
   `WorkspaceHints` says the process working directory. These cannot both be right, and #214 is
   what the disagreement costs. Which was meant?
3. **Does `rose_format` intend to be the check?** Its description says "pass apply=false to check
   formatting without writing", and #218 shows it passing a file `dotnet format` fails. Is the
   contract "what IDE0055 thinks" or "what CI will think"? They are different tools.
4. **Is there a reason `rose_find_implementations` has no `project`?** It looks like an omission
   rather than a decision, but `rose_find_references` grew one and this did not, so it may have been
   considered.
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
| `rose_outline` `WorkspaceManager`, compact | "What does this class contain", cheaply | **Lost.** 10.1 KB against 748 bytes of grep for the same question (AGT-01). The grep answered; the outline answered and cost thirteen times as much. |
| `rose_outline` `WorkspaceManager`, full | The same, with signatures and docs | Worked, 22.3 KB. Genuinely more informative than grep -- base types, `isGenerated`, accessibility -- but I would not spend that twice in a session, and the documentation half was supposed to be one line per member (AGT-02). |
| `rose_symbol_info` `Microsoft.CodeAnalysis.Workspace.CurrentSolution` | Reproduce the metadata failure another reviewer hit | **Worked**, which is the interesting part: it disproved "metadata is broken" and led to the real rule (AGT-03). |
| `rose_symbol_info` `ModelContextProtocol.Server.McpServer.SessionId` | The reviewer's actual case | **Failed**, with five unrelated source symbols offered as candidates. |
| `rose_symbol_info` `System.Collections.Generic.List` | Wrong arity on a metadata type | **Failed**, offering four methods named `List` in Rose's own source. No mention of arity, none of metadata. |
| `rose_symbol_info` `ModelContextProtocol.Server.McpServerTool` | What the SDK says about tool errors | Worked, and was the authority for the `isError`-versus-thrown question in this review -- but 9.2 KB of raw XML (AGT-11). Rose answered a question about its own dependency that I had no other way to ask, which is a real win despite the size. |
| `rose_symbol_info` `RoseMcp.Broker.WorkspaceManagr.CallAsync` (typo) | Grade the near-miss error | **Excellent.** The fix was in the message. |
| `rose_find_references` `ToolNames.WorkspaceStatus` | 16 call sites by containing member | Worked. Beat grep on precision: grep for `WorkspaceStatus` also matches `WorkspaceStatusReport`, `WorkspaceStatusReporter` and the tool name in strings. Lost on shape: a flat list with the absolute path 17 times (AGT-06). |
| `rose_find_references` same, `definitionsOnly=true` | Just the count | Worked, but `truncated: true` over an empty list (AGT-05). |
| `rose_search_symbols` `ToolErrorReporting` | Find the three copies | **Excellent, and beat grep outright.** Three addresses ready to paste into the next call; `find -name` would have given me paths and no addresses. |
| `rose_resolve_name` `ToolErrorReporting` (no filePath) | Ambiguous short name | **Failed** with a leaked Roslyn parameter name (AGT-04). |
| `rose_resolve_name` `ToolErrorReporting` + filePath | The same question, scoped | **Excellent.** "in scope already, so the error is something else: a misspelling, an accessibility problem, or the wrong number of type arguments" is the best single sentence on the surface. |
| `rose_resolve_name` `Encoding` (no filePath) | Control, to isolate the failure above | Worked. So the failure is the argument shape, not the tool. |
| `rose_diagnostics` one file, then the solution | "Does it compile" | **Worked, and is the strongest thing in the product.** Whole solution, 18 projects, a few seconds, against a `dotnet build` of 30-60 s. Every agentic session pays that difference dozens of times. Marked down only for saying nothing about the workspace being degraded (AGT-12). |
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
