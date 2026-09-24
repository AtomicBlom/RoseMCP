# Usability and product value of the shipped user interfaces

**Scope.** A product review of the three interfaces RoseMCP puts in front of a person, read as
layouts rather than as code.

- Tray: `src/RoseMcp.Tray/MainWindow.xaml` (442 lines), `MainWindow.xaml.cs`, `WorkspaceRow.cs`;
  `src/RoseMcp.Ui.Core/SessionRow.cs`, `ActivityRow.cs`, `Format.cs`.
- Inspector: `src/RoseMcp.Inspector/MainWindow.xaml`, `Controls/ExecutionBar.xaml`,
  `Panes/{EventsPane,BreakpointsPane,StackPane,ThreadsPane,XamlPane}.xaml` and their code-behind;
  `src/RoseMcp.Ui.Core/Inspector/*` (`HoldKeeper`, `InspectorText`, the row types).
- In-app Rose panel: `src/RoseMcp.Xaml.Tap/tap_overlay.h`, `tap_tool_zoom.h`, `tap_tool_rulers.h`,
  `tap_widgets.h`, `tap_measure.h`, `tap_pick.h`.
- Contracts the windows draw from: `WorkspaceSummary`, `WorkspaceStatusReport`, `ProjectStatus`,
  `WorkerActivity`, `LiveAppSessionSummary`.
- `docs/decisions/{the-inspector-is-a-client-of-the-broker,debugging-ui,the-in-app-toolbar-is-a-click-through-overlay,a-pick-honours-hit-testing-and-prefers-the-apps-markup}.md`;
  `docs/invariants/overlay.md`; `README.md` "Transports" and "Debugging and XAML"; wiki pages
  *The Rose panel*, *The inspector*; issues #221-#226, #228, #160, #112.

A separate reviewer owns the UI's code structure in `05-ui-tests-and-process.md` (findings UIP-01 to
UIP-12). Structural causes are named in one clause and referenced by ID; they are not re-argued here.

**Verdict.** **Adequate, and aimed slightly but consistently wrong.** The owner's instinct is
correct, but the diagnosis he reached for -- "poorly designed" -- is not the problem. The craft is
high: the typography is consistent, the empty states are written rather than left blank
(`InspectorText` is genuinely exemplary), the tone system is coherent across two windows, and the
comments in the XAML show someone who reasoned about every placement. What is wrong is the *job*.
All three surfaces are built for a human **doing** the work, and RoseMCP's user is a human
**supervising an agent** doing it. That single misaim explains almost every specific weakness: it is
why the inspector holds four panes that are smaller versions of Visual Studio's and none that shows
what the agent is doing; it is why the tray's activity history -- the only record anywhere of what an
agent did to your solution -- is capped at eight entries and collapsed behind an expander; and it is
why the panel's pixel tools carry no explanation of the one thing that justifies them. The strongest single fact in this
review is that the broker computes a dozen facts specifically so a window can show them, documents in
each one's XML summary *why a reader needs it*, and no window renders them: `InfoAge`,
`InstallLocation`, `LiveAppSessionSummary.Notice`, the names in `FailedProjects`, every field of
`ProjectStatus`, and `AnalyzerLoadFailures`. Those are the cheapest wins available anywhere in the
repository and they are all in the same place.

Per surface: the tray is **useful** and closest to its job. The in-app panel contains the single most
valuable interaction in the product (`Select element`) wrapped in two tools that cost more than they
return. The inspector is **wrong-shaped**: competent, and competing with Visual Studio on Visual
Studio's terms, which it cannot win.

---

## 1. Surface inventory

| | Tray window | Inspector | In-app Rose panel |
|---|---|---|---|
| **The user** | The person running the broker on their own machine | The person whose app an agent is debugging | The person whose app is running, with their hands on it |
| **The job** | "Is Rose healthy, what is it costing me, and what is my agent doing to my solution" | "What is the agent doing to my process, why did it stop, do I agree" | "Point at the thing I am talking about" |
| **The moment they open it** | Something feels wrong (slow agent, suspicious answer); or once, on install, to copy the endpoint | The tray said a session exists and something is stopped, or they clicked Inspect | It is already there -- it is resident from the first XAML call and never opened deliberately |
| **What it shows** | Headline + subtitle; per-solution cards (state, memory, pid/uptime/config/projects/load time, degraded reasons, running calls, collapsed history); per-session cards (state, pids, XAML stack, stop reason, heartbeat, resume countdown) | Header (session, state, execution, facts); Pause/Continue/3 steps + hold; five tabs -- Events, Breakpoints, Stack, Threads, XAML | Idle / Select / Rulers / Just my XAML / Deselect / Magnify / Hide; a rulers readout row; a zoom row with a hex swatch |
| **Verdict** | **Useful** | **Wrong-shaped** | **Select: essential. Rulers: useful. Magnify: essential and unexplained.** |

Per-pane and per-tool verdicts:

| Element | Verdict | One-line reason |
|---|---|---|
| Tray: headline + subtitle | Essential | The only place that answers "is this healthy" in one glance, and it does |
| Tray: workspace card | Essential | Cost, state and the reload button in one place; the thing the window is for |
| Tray: running activity block | Essential | The only visible difference between a warm host and a wedged one |
| Tray: recent-operations expander | Useful, wrongly placed | It is the agent audit trail, capped at 8 and collapsed |
| Tray: session card | Useful | Good facts, no link to the solution the session's code comes from |
| Tray: 44px title bar holding the word "RoseMCP" | Decorative | 11% of the minimum window height, duplicated in taskbar and tray tooltip |
| Inspector: Events tab | Essential | The best pane in the window; a chronological narrative VS has no equivalent of |
| Inspector: Breakpoints tab | Inverted | The compose form (a driving feature) is on top; the list (the supervision feature) is below a rule |
| Inspector: Stack tab | Useful, mis-laid-out | Right material for "why did it stop", laid out as VS's Call Stack + Locals |
| Inspector: Threads tab | Should not exist | Read-only, answers no supervision question, and costs a hold on the user's app to show |
| Inspector: XAML tab | Essential | The most distinctive pane, and the one users have filed four issues against |
| Inspector: ExecutionBar + `HoldKeeper` | Essential | The one primitive in the product Visual Studio has no analogue for |
| Panel: Select element | Essential | The whole product thesis in one button |
| Panel: Just my XAML | Essential | And correctly the default |
| Panel: Deselect / Idle / Hide | Necessary plumbing | Fine; "Idle" reads as a status, not a button |
| Panel: Rulers (734 lines) | Useful | Real layout work, but the numbers cannot be handed to the agent |
| Panel: Magnify / Pixel lens / Scale (739 lines) | Essential, unexplained | The OS magnifier filters bilinearly and cannot be told not to, so it can answer neither question this answers; nothing in the repo says so (USE-08) |

---

## 2. ASCII sketches, as they are today

### The tray window (780x620 initial, 560x400 minimum)

```
+--------------------------------------------------------------------------------+
| [.] RoseMCP                                             [ - ] [ [] ] [ X ]      |  44px, drag only
+--------------------------------------------------------------------------------+
|                                                                                 |
|  2 solutions loaded, debugging 1 session      [ (o) http://127.0.0.1:5077 [] ]  |  24pt headline
|  1.4 GB working set  .  1 operation running  .  1 needs attention   [ ... ]     |  subtitle + More
|                                                                                 |
+--------------------------------------------------------------------------------+
| (i) <notice: a contained crash, an inspector that is not installed>       [x]   |  only sometimes
+--------------------------------------------------------------------------------+
| |                                                                             | |
| | | RoseMCP.slnx    [ (*) Loaded ]                  842 MB   [D] [O] [X]      | |  4px tone stem
| | |                                                 611 MB heap               | |
| | | C:\Dev\Personal\RoseMCP\RoseMcp.slnx                                      | |
| | | pid 24180 . up 2h 13m . Debug|Any CPU . 18 projects . loaded in 24.3s     | |
| | |                                                                           | |
| | | +-----------------------------------------------------------------------+ | |
| | | | /!\  Answers may be incomplete                                        | | |  InfoBar, only
| | | |     2 analyzer assemblies would not load. Rebuild them and reload.    | | |  when degraded
| | | +-----------------------------------------------------------------------+ | |
| | |                                                                           | |
| | | find references   WorkspaceManager                                   3.2s | |  running block
| | | [##############################-------------------------------]           | |
| | | scanning 14 of 18 projects                                                | |
| | |                                                                           | |
| | | v  8 recent operations, 1 failed                                          | |  COLLAPSED
| | +---------------------------------------------------------------------------+ |
| |                                                                             | |
| | | OtherApp.sln    [ (o) Loading ]                  --      [D] [O] [X]      | |
| | +---------------------------------------------------------------------------+ |
| |                                                                             | |
| |  Debug sessions                                                             | |
| |                                                                             | |
| | | SampleUwpApp (pid 9876)  [ (*) Stopped ]  [(o) Inspect] [L] [X]           | |
| | | pid 9876 . host 4321 . x64 . up 3m                                        | |
| | | UWP  .  provider resident                                                 | |
| | | breakpoint bp-3 on thread 7                                     <- accent | |
| | | last event 0.4s ago    resumes in 1m 47s                                  | |
| | | v  4 recent calls                                                         | |  COLLAPSED
| | +---------------------------------------------------------------------------+ |
+--------------------------------------------------------------------------------+

[D] Explorer   [O] Reload   [X] Close      [L] host log
```

Empty state, replacing the whole scroller:

```
+--------------------------------------------------------------------------------+
|                                  (rose mark)                                    |
|     A solution is loaded the first time a client asks about it, and stays       |
|              warm here for every session after that.                            |
|                                                                                 |
|                    Register the endpoint with your agent:                       |
|      [ claude mcp add --transport http rose http://127.0.0.1:5077      [] ]     |
+--------------------------------------------------------------------------------+
```

### The inspector window (980x720 initial, 640x480 minimum)

```
+---------------------------------------------------------------------------------------+
| (o) RoseMCP Inspector                                            [ - ] [ [] ] [ X ]    |  40px, drag only
+---------------------------------------------------------------------------------------+
| +-----------------------------------------------------------------------------------+ |
| | SampleUwpApp (pid 9876)  [Running]  [breakpoint bp-3 . 1m 47s]   (o) [Host log]    | |
| |                                                                     [Detach] [cog] | |
| | pid 9876 . host 4321 . x64 . up 3m                                                 | |
| | UWP  .  provider resident                                                          | |
| +-----------------------------------------------------------------------------------+ |
+---------------------------------------------------------------------------------------+
| [Pause] [Continue] [Step in] [Step over] [Step out]   held, resumes in 1m 47s [Release]|
+---------------------------------------------------------------------------------------+
| (i) <notice>                                                                     [x]  |  only sometimes
+---------------------------------------------------------------------------------------+
|  Events | Breakpoints | Stack | Threads | XAML                                         |  SelectorBar
+---------------------------------------------------------------------------------------+
|                                                                                       |
|   ... one of five panes, toggled by Visibility ...                                    |
|                                                                                       |
+---------------------------------------------------------------------------------------+
```

Pane 1 -- **Events** (the default tab):

```
[Everything] [Stops] [Exceptions] [Log output]     showing 214 of 1,308
+-------------------------------------------------------------------------+
| (i) 412 earlier events were dropped by the host's buffer.                |
+-------------------------------------------------------------------------+
| 14:22:07.118  [module]   loaded System.Text.Json.dll                     |
| 14:22:09.402  [log]  t7  Refreshing the widget list                      |
| 14:22:11.883  [exception] t7  Object reference not set to an instance    |
|               System.NullReferenceException                              |
|               v  stack and locals                                        |
| 14:22:12.004  [stop] t7  breakpoint bp-3 at Widget.Refresh:41            |
|               v  stack and locals                                        |
+-------------------------------------------------------------------------+   anchored at bottom
```

Pane 2 -- **Breakpoints** (252 lines of markup, the largest pane):

```
+-------------------------------------------------------------------------+
| Add a breakpoint or tracepoint                                          |  <- accent card,
|  [ (Q) Find a method or property                                     ]  |     ~60% of the pane
|  Type part of a method or property name to search the target's loaded   |
|  modules. Widget.Refresh narrows it; two characters is the shortest.    |
|                                                                         |
|  Widget.Refresh()                                                 [x]   |
|  C:\Dev\Sample\Widget.cs                                                |
|  +-------------------------------------------------------------------+ |
|  |  38   {                                                           | |  every line is a
|  |  39       var items = _store.All();               IL_0000         | |  button; unbreakable
|  |  40       if (items.Count == 0) return;           IL_000C         | |  lines are disabled
|  |  41       Render(items);              <- chosen   IL_0019         | |
|  +-------------------------------------------------------------------+ |
|  ( Widget.Refresh . line 41 . IL_0019 )                                 |
|  [Hold: sec] [Condition: count > 3        ] [Every Nth]                 |
|  [Log message: optional, tracepoints only                            ]  |
|  [Add breakpoint] [Add tracepoint]                                      |
+-------------------------------------------------------------------------+
--------------------------------------------------------------------------- <- rule
 2 breakpoints                                                               <- THE SUPERVISION
| Widget.Refresh@IL_0019                        [bound]   3 hits     [x]  |     VIEW IS HERE,
|   Widget.cs:41                                                          |     below the fold
|   hold 30s, count > 3                                                   |
 1 tracepoint
| Store.Save@IL_0000                            [bound]  84 hits     [x]  |
```

Pane 3 -- **Stack**:

```
+-------------------------------------------------------------------------+
| (i) The target continued -- an agent, a step, or the safety timer.       |  only when stale
+-------------------------------------------------------------------------+
| 4 frames              | Widget.cs                                       |
| +-------------------+ | +---------------------------------------------+ |
| | 0  Widget.Refresh | | |  39   var items = _store.All();             | |
| |    Widget.cs:41   | | |  40   if (items.Count == 0) return;         | |
| | 1  Page.OnLoaded  | | |  41   Render(items);          <- highlighted | |
| |    Page.cs:88     | | |  42 }                                       | |
| | 2  <native x3>    | | +---------------------------------------------+ |
| | 3  Program.Main   | | Locals                                          |
| +-------------------+ | +---------------------------------------------+ |
|      fixed 320px      | | > items   Count = 14   List<Widget>         | |  MaxHeight=300,
|                       | |   index   3            Int32                | |  SelectionMode=None
|                       | +---------------------------------------------+ |  (nothing copyable)
+-------------------------------------------------------------------------+
```

Pane 4 -- **Threads**:

```
 6 threads                                            held, resumes in 1m 47s
+-------------------------------------------------------------------------+
|     7 | Widget.Refresh                              [ stopped here ]    |
|       | Running                                                         |
|    12 | Task.DelayPromise.CompleteTimedOut                              |
|       | WaitSleepJoin                                                   |
|    14 | (no managed frames)                                             |
+-------------------------------------------------------------------------+
```

Pane 5 -- **XAML**:

```
[Refresh] [Pick from app] [Just my XAML] [Deselect]        [ ] Include defaults
+-------------------------------------------------------------------------+
| (i) <why nothing can be read right now>                                 |
+-------------------------------------------------------------------------+
| 412 elements            | Border  #WidgetHost                            |
| +---------------------+ | ( /Grid[0]/Border[2] ) [Copy]  Page.xaml:31    |
| | v Page              | | +-------------------------------------------+  |
| |   v Grid            | | | | Margin        8,4,8,4            Local  |  |
| |     > Border #Widg..| | | | Background    #FF2B2B2B          Style  |  |
| |       > StackPanel  | | | | FontSize      14                 Inher. |  |
| |         TextBlock   | | | | Padding       0,0,0,0            Default|  |
| +---------------------+ | +-------------------------------------------+  |
|      fixed 380px        | Reading an element's properties brings its     |
|                         | collection properties into existence...        |
+-------------------------------------------------------------------------+
        ^ bar colour = provenance: Local / Style / Inherited / Animation / Default
```

### The in-app Rose panel (drawn inside the user's running app)

```
        +-------------------------------------------------------------------+
        | ::  (o)  [Idle] [Select] [Rulers] | [My XAML] [Deselect] | [Zoom]  |  row 1, always
        |                                                          [Hide]   |
        | ( 312x48 ) ( Margin 8,4,8,4 ) ( Padding none )                     |  row 2, rulers only
        | [Scale app] [Pixel lens] [-] [+]  ( #2B2B2B )                      |  row 3, zoom only
        +-------------------------------------------------------------------+

                         ...over the app itself...

        +=================================+
        # Border  #WidgetHost             #  <- selection outline + badge
        #   +-------------------------+   #
        #   |  the app's own content  |   #     with Rulers armed, dimension
        #   +-------------------------+   #     lines and numbers to whatever
        +=================================+     the pointer is over
                  |<-- 24 -->|
```

Collapsed, it is the grip alone: `[ :: ]`.

---

## 3. What does a user most want to know? (the spine of this review)

For each surface: the questions a user actually arrives with, ranked by how often they arrive with
them, then marked against what the window does.

### The tray window

| # | The question they arrived with | How it does | Why |
|---|---|---|---|
| 1 | Is Rose running, and is it healthy? | **Answered well** | Headline, subtitle and the tone stem down each card. One glance. Best thing in the product. |
| 2 | How much RAM is this costing me, and can I get some back? | **Answered well** | Working set per card and summed in the subtitle, heap beside it, Close on every card and Close all in the menu. |
| 3 | Which solutions are warm, and what did they cost to warm? | **Answered well** | `pid . up . config . N projects . loaded in 24.3s` is exactly the line. |
| 4 | Why is my agent slow *right now*? | **Answered badly** | The running block is right -- label, target, elapsed, progress, the worker's last word. But with several agents on one http broker (the arrangement the README pitches) nothing says *whose* call it is: `WorkerActivity` has no client, session or origin field, though `CallSession.Id` and `CallOrigin.Directory` both exist in the broker. And there is no aggregate: no call count, no median latency, nothing that separates "this call is slow" from "Rose is slow". |
| 5 | What just went wrong? | **Answered but buried** | Every failure in the product lands in one place: a collapsed `Expander` per card, capped at 8 entries (`ActivityLog.RecentPerWorkspace`), whose header reads `8 recent operations, 1 failed` in tertiary caption grey. The subtitle counts *degraded workspaces*, not *failed calls*, so a card can run 30 failing calls a minute while the headline says "2 solutions loaded" and the subtitle says "idle". |
| 6 | Let me restart the one that is wedged | **Answered well** | Reload and Close per card, with tooltips that explain the difference in a sentence each. The button does not disable during the await, so a 30-second reload looks inert until the next poll flips the pill -- minor, and the pill does flip. |
| 7 | Can I trust the answer Rose just gave my agent? | **Not answered** | The highest-value question RoseMCP can answer, and the reason the project exists. The top-line degraded reason is shown; everything under it is unreachable. See USE-01. |
| 8 | What has the agent been doing to my solution all afternoon? | **Not answered** | Eight entries, dropped on close, explicitly "live state for a UI, not an audit trail". There is no history surface anywhere except raw log files behind a menu item that opens a folder. |
| 9 | Which solution does that debug session belong to? | **Not answered** | `LiveAppSessionSummary` has no workspace field at all. The window's two sections are unrelated lists that happen to share a scroller. |

### The inspector

| # | The question they arrived with | How it does | Why |
|---|---|---|---|
| 1 | What is the agent doing to my process? | **Not answered** | `LiveAppSessionSummary.Running` and `.Recent` carry the agent's tool calls with names, targets, elapsed times and outcomes. `MainWindow` holds them on `_row` and `_summary` and renders neither. The *tray* renders them. See USE-02. |
| 2 | Why did it stop? | **Answered, scattered** | The execution pill says `breakpoint bp-3 . 1m 47s`; the Events tail has the stop entry with frames and locals; the Stack tab has the frame and the highlighted line. Three places, and the reader assembles them. |
| 3 | Let me hold it while I look / let it go | **Answered well** | `HoldKeeper` plus the ExecutionBar. The most product-distinctive thing in the window, and correctly built. |
| 4 | What does the app look like, and what is this element? | **Answered, with known gaps** | The XAML pane is the second-most distinctive thing here. #222 (no search), #223 (no child counts), #224 (no grouping by source file) and #225 (dead while stopped) are all real and all filed. |
| 5 | What breakpoints did the agent set, and have they hit? | **Answered but buried** | `BreakpointRow` carries `BoundLabel`, `Hits`, `Source`, `Conditions` and `Detail` -- everything a supervisor wants. It sits below a rule, under a compose form occupying the top ~60% of the pane. |
| 6 | Where is it in my code? | **Answered badly** | Read-only source, no navigation, no go-to-definition, no edit, no search. Visual Studio wins on every axis. |
| 7 | Let me copy that value / that frame and paste it to the agent | **Not answered** | `VariableRows` is `SelectionMode="None"`; `FrameRows` is `SelectionMode="Single"` with no copy. In a window built for a conversation with an agent, nothing but the XAML element address can be copied out. |
| 8 | Do I agree with what the agent is about to do? | **Not answered** | Nothing in the product has a concept of a pending agent action. This is the strategic gap, not a defect. |
| 9 | Is what I am looking at still true? | **Not answered** | `LiveAppSessionSummary.InfoAge` exists precisely for this, with a docstring arguing for it, and is rendered nowhere. See USE-03. |

### The in-app Rose panel

| # | The question they arrived with | How it does | Why |
|---|---|---|---|
| 1 | Make the agent look at *this* thing | **Answered well** | Select plus Just my XAML, defaulting to the app's own markup, honouring hit testing, returning the whole stack under the click. This is the product. |
| 2 | Where are these pixels coming from? | **Answered well** | Rulers draws the box model, and the numbers say which sides are zero and whether the type has padding at all -- a distinction the bands genuinely cannot draw. |
| 3 | How far apart are these two things? | **Answered, transiently** | Measure runs anchor to whatever the pointer is over. You cannot hold two elements, and the measurement cannot be copied, named or handed to the agent. |
| 4 | Now show me that element's properties | **Not answered** | The panel selects; the window that shows the selection is another application with no link to it. Alt-tab, find the tray, find the session, click Inspect. #226 is exactly this and diagnoses it correctly. |
| 5 | Did the agent's edit land, and on what? | **Not answered** | `rose_xaml_apply` succeeds and nothing in the app marks what changed. The overlay already knows how to draw an outline on a handle. |
| 6 | Tell the agent this is wrong | **Not answered** | No note, no copy-the-address, no "flag this". The selection is a fact the agent has to think to ask about. |
| 7 | What colour is that pixel, exactly? | **Answered, uniquely** | And nothing says why it had to be built. The OS magnifier bilinear-filters with no way to disable it, so it cannot give an accurate colour or show a one-pixel gap at a corner radius. That reason is absent from the code, the invariant and the wiki (USE-08). |

### Where the pitch and the code disagree

- The wiki's *Rose panel* page gives **Magnify** roughly equal billing with **Select**, including a
  screenshot captioned "the app itself at 4x". The billing is earned -- the OS magnifier cannot
  answer either question this one answers -- but the page never says so, so a reader cannot tell it
  apart from a toy. Meanwhile the page's own best sentence, "tell the agent to look at the element I
  selected", is the product and is one line in a list.
- The wiki's *inspector* page describes the window as for "the person running the broker who needs
  visibility **across** debugging sessions". `MainWindow`'s own class summary says the opposite, and
  means it: "There is deliberately no list of other sessions here ... a window that could be
  re-pointed at another process would make its own title a lie." Whichever is right, the page tells
  a reader to expect a console and hands them a single-process window. (The fetched summary also
  says "six primary tabs" and then lists five; the code has five. Treat that one as likely
  summarisation noise rather than a page defect.)
- `README.md` says the inspector "shows sessions an agent started" -- true, and the closest the
  documentation comes to naming the supervision job. It then lists what the window holds: "the event
  tail, breakpoints and tracepoints, the call stack with its source and values, threads, and the
  live visual tree". Every item on that list except the tree is a Visual Studio pane. The sentence
  sells the product; the list sells the clone.

---

## Strengths

These must survive any redesign.

**The tray's headline and subtitle pair.** `MainWindow.DescribeHeadline` and `DescribeSubtitle`
compress the whole machine into two lines a person reads in under a second, and the composition
rules are right: the subtitle only mentions attention and held targets when there are some, so the
absence of a clause is itself information. `DescribeTooltip` does the same job in four words for a
16px icon.

**`Format`.** `src/RoseMcp.Ui.Core/Format.cs` is 117 lines and every one has a reason. `Age`
returning `--` rather than `0s ago` because "nothing having happened yet and something happening this
instant are opposite facts"; `Countdown` clamping at "now" rather than counting upwards; `FileLine`
cutting on both separators because the path came out of a PDB written by another machine. This is
the level of care the rest of the UI should be judged against.

**`InspectorText`.** Every empty state in the inspector, gathered in one testable file, each saying
what happened *and what to do about it*. `NoMethodsFound` explains that the search covers loaded
modules, so code the target has not reached is not there to find -- one sentence that prevents a bug
report. This is the best piece of product writing in the repository, and the pattern should be
copied into the tray, which writes its empty states inline in XAML.

**`HoldKeeper`.** The one genuinely new idea in the UI layer. Readers declare what they want rather
than issuing commands and the keeper reconciles; `Want` is idempotent so a poll cannot storm the
host; the hold is dropped when the stop sequence moves; and the `Task.Yield()` in `SettleAsync`
exists so that a tab change -- which tells the arriving pane and the leaving pane in one pass --
collapses into one decision instead of a release-then-take that would let the app slip away. Keep it,
and build the window around it.

**The tone system.** Five tones, one element per tone each carrying its own `ThemeResource` brush,
with the comment explaining that a brush chosen in code would be the brush for whichever theme was in
force when it was chosen. Consistent across `WorkspaceRow` and `SessionRow`, stem and pill agreeing.

**Property provenance in the XAML pane.** The five-bucket grouping (Local, Style, Inherited,
Animation, other, Default) with a colour bar rather than coloured text is better than Visual Studio's
Live Property Explorer for the question people actually have, which is "which file do I go and edit".
The decision record's note that a substring match would file `DefaultStyle` under Default -- "a
confident wrong answer about which file to go and edit" -- is the right instinct in the right place.

**The in-app pick.** Honouring hit testing with `includeAllElements` false; preferring the app's own
markup by URI scheme rather than by a name heuristic; falling back rather than emptying the filter on
an app with no source info; and returning the whole stack under the click because "the wanted element
is usually a step away". Four correct decisions in one feature.

**The click-through model.** A `Background` of null takes no part in hit testing, so the overlay's
root passes every click to the app while the toolbar takes its own. No hooks, and no injected
elements in the app's tree. The app stays usable, which is what makes a resident toolbar acceptable
at all.

---

## Findings

### USE-01 The tray can say a workspace is degraded but never why, because `WorkspaceSummary` throws away the report that says why

- **Severity:** High   (blocks the stated goal: the project exists to stop confidently wrong answers)
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/WorkspaceSummary.cs`, `src/RoseMcp.Contracts/WorkspaceStatusReport.cs:45-70`,
  `src/RoseMcp.Tray/WorkspaceRow.cs:253-310`
- **What:** `WorkspaceStatusReport` carries `Projects` (a full `ProjectStatus` each),
  `AnalyzerLoadFailures` (assembly, wanted version, HRESULT), `Restore`, `LoadDiagnostics` with a
  pre-fold count, and `AvailableConfigurations`. `WorkspaceSummary` -- the only thing a window ever
  sees -- carries none of them. It keeps `DegradedReasons` (already folded to one line per kind),
  `FailedProjects` as a list of names, and `ProjectCount`. `WorkspaceRow.DescribeFacts` then reduces
  even the names to `$"{failed} failed to load"`, so the names are computed, transported, and thrown
  away in the last line before the screen.

  The fold is deliberate and well argued -- `DegradedReasons` documents it: "a reason is an index
  into this report rather than a second copy of it", listing `ProjectStatus.UnresolvedXamlTypes`,
  `ProjectStatus.MissingAnalyzerOutputs`, `AnalyzerLoadFailures` and `RestoreReport.Unrestored` as
  where the particulars live. The same docstring says "The tray draws this whole list into a single
  information bar". Both halves are true and together they are a bug: the tray is given the index and
  not the thing indexed, so the fold has nowhere to unfold to.
- **Why it matters:** This repository's own `rose_workspace_status` reports Degraded because two
  generator assemblies fail a manifest version check (10.0.14 located against a pinned 10.0.11). The
  tray can show "Answers may be incomplete" and one folded sentence. It cannot show which assembly,
  which version, or the HRESULT -- and it cannot show that three specific projects failed their
  design-time build, which is the fact that decides whether the answer an agent just acted on was
  about real code or about an empty project. A person who suspects a wrong answer has exactly one
  next step, which is to open a log folder and read Serilog output. The single most valuable thing
  RoseMCP knows is one hop from the screen and does not make it.
- **Suggested change:** Add an expandable per-workspace detail section fed by a real
  `WorkspaceStatusReport`, fetched on demand rather than on every poll (a "Details" disclosure on the
  health InfoBar, one `GET /admin/workspaces/{key}/status`). Inside it: a project list with the
  failed ones first and each carrying `TargetFramework`, `GeneratorCount` vs `GeneratedDocumentCount`,
  `MissingAnalyzerOutputs`, `XamlStubbedCount` and `UnresolvedXamlTypes`; then `AnalyzerLoadFailures`
  verbatim; then `Restore`. Pattern: *master-detail on demand* -- the summary stays cheap enough to
  poll at 400ms and the report is read only when a person asks. The minimum viable version is three
  lines: show `FailedProjects` as names in the health InfoBar instead of as a count.

### USE-02 The inspector -- the window for watching an agent debug -- shows nothing about what the agent is doing, and holds the data

- **Severity:** High   (blocks the stated goal of the window)
- **Effort:** S
- **Where:** `src/RoseMcp.Inspector/MainWindow.xaml` (no activity element anywhere),
  `src/RoseMcp.Inspector/MainWindow.xaml.cs:58,312-318`, `src/RoseMcp.Ui.Core/SessionRow.cs:208-230`
- **What:** `LiveAppSessionSummary.Running` and `.Recent` carry every tool call made against the
  session -- `rose_debug_set_breakpoint`, `rose_debug_evaluate`, `rose_xaml_apply` -- with target,
  elapsed time, progress, outcome and error. `SessionRow` already builds `ActivityRow`s for both.
  `MainWindow` constructs a `SessionRow`, keeps it in `_row`, reads five scalar properties off it
  (`DisplayName`, `StateLabel`, `Facts`, `XamlFact`, `ExecutionLabel`) and never touches `Running` or
  `Recent`. The tray renders both, for the same session, from the same row type.

  The one place the inspector *uses* agent activity is `SessionRow.AppliedXaml`, which watches the
  recent list for a completed `rose_xaml_apply` so the XAML pane can re-read the tree. So the window
  detects that an agent changed the app, silently refreshes, and tells the reader nothing about what
  changed or who changed it.
- **Why it matters:** This is the window's job. A person watching an agent debug their app wants,
  first, "what is it doing" -- and gets a Visual Studio-shaped window with five tabs, none of which
  mentions the agent. Without it the human cannot distinguish "the app stopped because the agent set
  a breakpoint thirty seconds ago" from "the app stopped and nobody knows why", which is the whole
  supervision question. The data is in memory, typed, formatted, and one `ItemsRepeater` away.
- **Suggested change:** Put the agent's activity in the header, permanently, above the tabs: the
  running call as a live line (the tray's `RunningActivityTemplate` is already a shared
  `DataTemplate` and could be lifted into `RoseMcp.Ui`), and the last few finished calls as a
  one-line strip. Longer term make it tab 1 and call it "Agent" -- see the redesign below. Pattern:
  *the subject of the window goes in the chrome, not in a tab*.

### USE-03 Three session facts are computed, documented with a paragraph each explaining why a reader needs them, and rendered by no window

- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Contracts/LiveAppSessionSummary.cs` -- `InfoAge`, `InstallLocation`,
  `Notice`; `src/RoseMcp.Ui.Core/SessionRow.cs` (no property for any of them)
- **What:** Verified by search across `src/`: `InfoAge` is written by `LiveAppSession.cs:199` and read
  by nothing; `InstallLocation` is read by nothing outside the contracts XML; `Notice` has no
  corresponding property on `SessionRow` and no element in either window's XAML.

  Each carries its own argument for existing:
  - `InfoAge` -- "Reported because the fields it qualifies are the ones a reader acts on. A session
    whose host has stopped answering keeps describing itself accurately as of some moment, and
    without this there is nothing to say which moment that was."
  - `InstallLocation` -- "on the summary rather than only in the event stream because a caller that
    reads one result and then works for an hour never goes back to the events, and a stale
    registration is invisible in every other field."
  - `Notice` -- "Kept apart from `Detail` on purpose ... an inspector that was asked for and could
    not open is the case it exists for."
- **Why it matters:** `InfoAge` is the worst of the three. The tray and the inspector both show a
  heartbeat derived from `LastEventAge` -- how long since the *target* produced an event. Nothing
  shows how long since the *host* answered. So a host that has stopped answering shows a frozen
  heartbeat, which reads identically to a target that is simply idle, and every other number on the
  card is silently stale. That is the exact failure the field's docstring describes, and the field
  that prevents it is not on screen. `Notice`'s absence means the broker's designed channel for
  "your session is fine but the inspector could not open" goes nowhere -- and the inspector-launch
  failure is precisely the case it was built for.
- **Suggested change:** `InfoAge` beside the heartbeat, shown only past a threshold ("host last
  answered 14s ago") so it is silent when healthy. `Notice` into the existing session-card InfoBar,
  Informational severity, distinct from the Error one bound to `IsFaulted`. `InstallLocation` into
  the session facts line for UWP targets only. All three are one property on `SessionRow` and one
  `TextBlock` each. Inversion: a contract property with a docstring arguing for a reader and no
  reader is a lint-able condition -- see the inversions section.

### USE-04 The only record of what an agent did to your solution is eight entries in a collapsed expander, discarded when the workspace closes

- **Severity:** High   (data loss, in the sense that matters here: the record a person needs is gone)
- **Effort:** M
- **Where:** `src/RoseMcp.Broker/ActivityLog.cs:20-24` (`RecentPerWorkspace = 8`, `Forget`),
  `src/RoseMcp.Tray/MainWindow.xaml:269-285`
- **What:** `ActivityLog` keeps eight finished operations per workspace and says so in a comment:
  "Enough history to answer 'what did that agent just do to my solution' and no more. This is live
  state for a UI, not an audit trail; logs are where a permanent record belongs." The tray renders
  them in an `Expander` that is collapsed by default, inside a card, with a tertiary-grey header. A
  `Forget` on close drops them.
- **Why it matters:** The comment names the question correctly and then answers a different one. "What
  did that agent just do to my solution" is not a question about the last eight calls; an agent doing
  a refactor runs dozens per minute, so the window is roughly twenty seconds wide. And it is asked
  *after the fact*, usually after something looks wrong -- which is exactly when the last eight calls
  are the eight that followed the one you care about. The fallback, "logs are where a permanent
  record belongs", is a folder of Serilog files opened by a menu item; nothing in the product turns
  that into an answer. Meanwhile this is the one thing RoseMCP knows that no other tool on the machine
  can: Visual Studio, git and the file watcher all see the *edit*, and only Rose saw the *call*.
- **Suggested change:** Two changes, in order of value. (1) Raise the cap substantially -- a few
  hundred `WorkerActivity` records is tens of kilobytes -- and keep the window's *display* to the
  last handful with a "show all" that opens a session-wide list. (2) Make the list a first-class
  surface rather than a per-card expander: one "Activity" view over all workspaces, newest first,
  filterable to failures. Pattern: *the audit trail is the product, not the debug output*. Add
  `CallOrigin.Directory` and `CallSession.Id` to `WorkerActivity` while doing it (USE-05).

### USE-05 With several agents on one broker -- the arrangement the README recommends -- nothing says which agent a call belongs to

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/WorkerActivity.cs`, `src/RoseMcp.Broker/ActivityLog.cs:33-47`,
  `src/RoseMcp.Broker/CallOrigin.cs`, `src/RoseMcp.Broker/CallSession.cs`
- **What:** `WorkerActivity` has `Operation`, `Target`, `StartedUtc`, `Elapsed`, `Outcome`, `Message`,
  `PercentComplete` and `Error`. It has no field naming the client. `ActivityLog.Begin` takes
  `(solutionPath, operation, target, upstream)` and does not capture `CallSession.Id` or
  `CallOrigin.Directory`, both of which are ambient at the moment `Begin` is called.
- **Why it matters:** README's "Transports" section sells exactly the multi-client case: "With a tray
  already running, a stdio server starts no workers of its own and relays to the tray ... so every
  session shares one warm worker per solution." Two Claude Code sessions in two worktrees of the same
  repository is the ordinary developer setup, and it is the setup this repository itself runs. In it
  the tray's running list is a mixture with no attribution: a `find references` that has been going
  for nine seconds cannot be traced to the terminal that asked for it, and a card full of failures
  cannot be traced to the agent producing them. The window that exists to answer "why is my agent
  slow" cannot say which agent.
- **Suggested change:** Capture both at `Begin` (they are `AsyncLocal`, so it is a field read), carry
  them on `WorkerActivity`, and show the origin directory's leaf as a quiet caption on each activity
  row. It also gives the activity view a filter. Pattern: *attribution is added once, at the one
  place that knows* -- the same rule `WorkspaceScopedResult` already applies to results.

### USE-06 The Breakpoints pane puts the driving feature on top and the supervision feature below the fold

- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Inspector/Panes/BreakpointsPane.xaml:17-150` (compose card),
  `:154-249` (the lists)
- **What:** The pane is a 252-line markup file in two halves. The top half is a compose form in an
  accented card: a method search, the chosen method, a scrollable source listing where every line is
  a button, a chosen-position pill, four inputs (Hold, Condition, Every Nth, Log message) and two
  Add buttons. It is `MaxHeight="260"` on the source alone, so the card is routinely taller than the
  visible pane. Below a rule sit the breakpoint and tracepoint lists.
- **Why it matters:** The lists are the supervision view -- what the agent set, where it bound, how
  many times it has hit, what condition it carries, and why it did not bind. On a window opened
  because an agent stopped the app, that is the first thing wanted and it is reached by scrolling
  past a form. The form itself is a good piece of design for a person with no IDE open (the
  method-search-then-click-a-line flow is better than Visual Studio's "break at function" dialog),
  but it is the flow of a human *driving*, and it is occupying the position of the thing a human
  *supervising* came for.
- **Suggested change:** Invert. Lists first, with a count line at the top ("3 breakpoints, 2 bound,
  84 hits"). The compose form becomes an "Add" button opening the same card in a flyout or an
  expander, collapsed by default. No logic changes; this is a reorder of two `StackPanel`s plus one
  toggle. Pattern: *the read view is the pane, the write view is a disclosure*.

### USE-07 The Threads pane earns less than it costs, and it costs a freeze of the user's application

- **Severity:** Medium
- **Effort:** S (to delete)
- **Where:** `src/RoseMcp.Inspector/Panes/ThreadsPane.xaml` (54 lines),
  `ThreadsPane.xaml.cs` (140 lines), `src/RoseMcp.Ui.Core/Inspector/ThreadRow.cs`
- **What:** A read-only list of managed threads: id, top frame, state, and a "stopped here" pill on
  one of them. No per-thread stack, no switching, no freeze or thaw, no naming. It is only
  answerable while the runtime is synchronized, so the pane claims the shared `HoldKeeper` hold while
  it is visible -- which keeps the user's application frozen for as long as the tab is open.
- **Why it matters:** Judged as a driving tool it is a strict subset of Visual Studio's Threads
  window, which has all of the above. Judged as a supervision tool, the question it could answer is
  "is the app deadlocked, and is the agent looking at the right thread" -- and a top-frame list
  half-answers the first and does not answer the second, because there is no way to look at another
  thread's stack from here. It is the only pane in the window that takes a hold on somebody's running
  application in exchange for a fact the header already carries (`breakpoint bp-3 on thread 7`).
- **Suggested change:** Delete the pane. Move the one fact that earns its place -- how many threads
  there are, and which one is stopped -- into the Stack pane's caption ("4 frames on thread 7 of 6").
  If per-thread stacks are ever wanted, they belong as a thread selector on the Stack pane, not as a
  tab. This is the clearest cut in the review.
### USE-08 The magnifier's reason for existing is written down nowhere, so a reader concludes it duplicates Windows Magnifier

- **Severity:** Medium
- **Effort:** S (a comment and a decision record); M if the crash class is to get a test
- **Where:** `src/RoseMcp.Xaml.Tap/tap_tool_zoom.h:1-11` (the file header), `:178-190` (the `Zoom`
  enum's comment), `docs/invariants/overlay.md:31-45`
- **What:** The pixel lens exists for a reason the code never states. **Windows Magnifier applies
  bilinear filtering that cannot be turned off.** That makes it unable to answer either question this
  tool exists for: you can never read an accurate colour out of a filtered image, and at 8x you
  cannot see that a `Border` with a corner radius has a one-pixel gap between its stroke and the
  background, because the filter smears the gap into the stroke. Nearest-neighbour replication of the
  captured pixels shows the device pixels the app actually produced, and the render-transform mode
  scales the live visual tree so text and vectors stay sharp rather than being resampled.

  The code argues the *implementation* alternative and never the *product* one. `tap_tool_zoom.h:183-190`
  explains at length why a `ScaleTransform` on an `Image` cannot be used, since UWP and WinUI expose
  no bitmap scaling mode and XAML therefore always filters, and the enum's comment names the two
  questions the lens answers. Neither says why the operating system's own magnifier cannot answer
  them. A reviewer arrives with "Windows ships this" and finds nothing in the repository that
  disagrees. **This review's first draft recommended deleting the feature on exactly that reasoning,
  and was wrong.**
- **Why it matters:** This is the repository's own comment convention, "why this and not that",
  applied to the alternative a maintainer would weigh and not to the alternative a *reviewer* or a
  *user* would weigh. The cost is not hypothetical: the feature came within one review of being cut,
  and a user who does not know about the filtering will reach for Win+plus, get a wrong colour, and
  never learn they were misled. The generalisation is worth taking across the product. Where a
  feature duplicates something the platform appears to offer, the reason the platform's version does
  not suffice is load-bearing documentation rather than trivia.
- **Suggested change:** Three things, none of them deletion.
  1. Put the reason in `tap_tool_zoom.h`'s file header in one sentence: Windows Magnifier filters
     bilinearly with no way to disable it, so it cannot answer "what colour is this exactly" or "is
     this edge one pixel or two", which are the two questions this tool exists for.
  2. Add a decision record, `the-magnifier-is-nearest-neighbour-because-the-os-one-is-not.md`, and
     link `overlay.md` to it. The invariant file explains why XAML cannot do it and inherits the
     same gap.
  3. Give the crash class a guard rather than a paragraph. The two recorded crashes, reading
     `PixelWidth` in a completion handler and capturing a `RenderTargetBitmap` in a lambda destroyed
     on a pool thread, are the strongest argument in the file for the lens being *hard*, not for it
     being *unwanted*. They are also untested, in the third of the product that never runs in CI
     (UIP-25).

  Surface the reason to the user too. The lens's tooltip says "a nearest-neighbour magnifier that
  follows the pointer, leaving the app untouched", which describes the mechanism. "Exact pixels and
  exact colours; the Windows magnifier smooths both" describes the benefit.



### USE-09 Nothing in the inspector can be copied, in a window whose purpose is feeding facts to an agent

- **Severity:** Medium
- **Effort:** S
- **Where:** `src/RoseMcp.Inspector/Panes/StackPane.xaml:18` (`SelectionMode="Single"`, no copy),
  `:90-91` (`SelectionMode="None"`), `Panes/EventsPane.xaml` (no copy affordance),
  `Panes/ThreadsPane.xaml:23` (`SelectionMode="None"`)
- **What:** The only copy button in the inspector is `CopyAddressButton` in the XAML pane. A frame, a
  source line, an exception with its stack, a variable's value, a thread's top frame: none of them
  can be selected, right-clicked or copied. Text is rendered into `TextBlock`s, which are not
  selectable by default.
- **Why it matters:** The interaction this whole product is built around is a human handing a fact to
  an agent. The human's channel to the agent is a terminal, so the physical act is copy and paste.
  A person who sees an exception in the tail and wants to say "here, look at this" retypes it. The
  `Copy` on the XAML address exists because somebody noticed exactly this need in one place and
  solved it there; the same need is in every pane.
- **Suggested change:** `IsTextSelectionEnabled="True"` on the message and source `TextBlock`s, and a
  right-click `MenuFlyout` with "Copy" on the frame, event and variable rows. Then one deliberate
  affordance worth more than all of them: **"Copy for the agent"** on a stop -- the stop reason, the
  frame, the file and line, and the top-frame variables as a paste-ready block. Pattern: *every fact
  a window shows is a fact somebody will want to quote*.

### USE-10 A pick in the app and the window that explains a pick are in different applications with no route between them

- **Severity:** Medium
- **Effort:** L (#226 has the design and it is genuinely hard)
- **Where:** `src/RoseMcp.Xaml.Tap/tap_overlay.h:660-755` (the toolbar has no such button),
  issue #226
- **What:** The flow the product is for -- see something wrong, click it, look at what it is -- goes:
  click the element in the app; alt-tab to find the tray (which may be hidden, since closing it hides
  rather than quits); find the right session card among several; click Inspect; wait for the window;
  change to the XAML tab; find the selected element. #226 diagnoses the cause correctly: the tap's
  pipe is strictly request-in reply-out, so the toolbar has nowhere to send a press, and the polling
  workaround needs the poller that is being launched.
- **Why it matters:** This is the one flow the in-app panel exists to start and it cannot finish it.
  Everything else on the panel is in service of a selection that then has to be looked at somewhere
  else. The distance between the click and the answer is the product's core latency, and it is six
  manual steps.
- **Suggested change:** Take #226's four-step route (named event, host latch, ride the existing
  once-a-second self-report, broker launches via `InspectorLauncher`). While that is being built,
  two cheap mitigations that need no native work: make the tray's `ShowInspectorOnAttach` preference
  default to on for a XAML target, so the window is already there; and have the inspector raise
  itself when the app's selection changes, which it already polls for twice a second.

### USE-11 A debug session is never linked to a solution, so the tray's two sections are unrelated lists

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Contracts/LiveAppSessionSummary.cs` (no workspace field),
  `src/RoseMcp.Tray/MainWindow.xaml:293-418`
- **What:** `WorkspaceSummary` extends `WorkspaceScopedResult` and therefore always names its
  workspace. `LiveAppSessionSummary` extends nothing and names none. The tray draws workspace cards,
  then a "Debug sessions" caption, then session cards, with nothing connecting a session to the
  solution whose code it is stopped in.
- **Why it matters:** On a machine with three solutions warm and two apps being debugged -- the case
  the tray is sized for -- "which of these is my app" is answered by reading process ids. More
  concretely, a person who sees `breakpoint bp-3 on thread 7` on a session card has no path from
  there to the workspace whose source would explain it, and a person who reloads a workspace has no
  warning that a session is debugging code from it. The window's own comment says the two sections
  share a scroller "because they are one machine's state" -- which is true and is also the only thing
  relating them.
- **Suggested change:** Carry the workspace key on the session summary where one is known (the attach
  and launch paths both resolve a solution), show it as a quiet caption on the session card, and
  nest or group sessions under their workspace card. Pattern: *the same attribution rule results
  already follow*.

### USE-12 At a breakpoint the Stack tab and the XAML tab are mutually exclusive, and that is the moment both are wanted

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Ui.Core/Inspector/InspectorText.cs` (`XamlNeedsARunningTarget`),
  `src/RoseMcp.Inspector/Panes/XamlPane.xaml.cs`, issue #225
- **What:** The XAML provider answers on the app's UI thread and the debugger holds that thread, so
  the whole XAML tab goes dead during a stop with an honest sentence saying why. The Stack tab is
  only useful during a stop. So the two panes are never useful at the same time.
- **Why it matters:** The reason a person opens this window at a breakpoint is to reconcile the code
  with the screen. #225 states it exactly: "the two things a reader wants at a breakpoint are the
  stack and the tree, and today having one costs the other." It is also the clearest case where
  Rose's supervision job differs from a debugger's driving job -- a driver reads the stack, a
  supervisor reads the stack *against what the app looks like*.
- **Suggested change:** #225's design is right: read the tree eagerly while the target runs, keep the
  last one, and say how old it is during the stop. The honesty requirement in that issue -- a caption
  saying when the tree was read, visually distinct from a live one -- is what makes it safe. Properties
  stay unavailable during the stop, scoped to the property pane rather than the whole tab.

### USE-13 "Open log folder" is the escape hatch from every unanswered question in the product, and it opens a folder

- **Severity:** Medium
- **Effort:** M
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml.cs:454-461`, `WorkspaceRow.DescribeHealth` (three of
  four branches end by pointing at the log folder)
- **What:** `DescribeHealth` says "Its log, under Open log folder, says why" for a crashed worker and
  "The worker's log, under Open log folder, has the details" for a failed load. `OnOpenHostLog` falls
  back to a directory when no file is named. `OnOpenLogs` deliberately opens the *parent* of the
  tray's own folder "because the question that brings someone here is usually which of them has the
  answer" -- an accurate description of the problem and a statement that the window is not solving it.
- **Why it matters:** Every dead end in the tray terminates at an Explorer window containing several
  Serilog files from several processes, with no correlation id between them (noted as BRK-15 in the
  broker review). The person then greps. That is the point at which a status window has stopped being
  a product and become a shortcut to the file system.
- **Suggested change:** Pick the file, not the folder, wherever one is known -- the worker's own log
  path is knowable per workspace, and the session already names `HostLogPath`. Beyond that, the real
  fix is USE-04: if the activity list held a few hundred entries with their errors, most trips to the
  log folder stop happening.

### USE-14 Rulers produces the numbers a layout conversation is about, and they cannot leave the app

- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Xaml.Tap/tap_tool_rulers.h:65-135`
- **What:** The rulers row shows `312x48`, `Margin 8,4,8,4`, `Padding none`, and the measurement
  overlay draws gaps to whatever the pointer is over. None of it is selectable, copyable or readable
  by the agent: `rose_xaml_properties` can report the element's properties, but the *measurement* --
  the gap between two elements, which is the thing a person is usually complaining about -- exists
  only as drawn pixels and vanishes when the pointer moves.
- **Why it matters:** "This gap should be 8, not 24" is the archetypal sentence in a layout
  conversation with an agent, and the tool that computes 24 cannot hand it over. The measurement is
  also anchored-to-hover, so it is transient: you cannot pin two elements and talk about the pair.
- **Suggested change:** Two small additions. Pin a measurement (click to fix the far end as well as
  the near one), and make the last measurement readable through the existing selection channel, so
  `rose_xaml_selection` returns anchor, target and the four gaps. That turns a picture into a fact
  the agent can act on, which is the difference between this tool and a screenshot.

### USE-15 The tray's two menus are maintained as two hand-written copies and already differ

- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml:75-89` (tray flyout) and `:121-159` (window flyout);
  `MainWindow.xaml.cs:469-507` (`OnMenuOpening` loops over pairs of named controls)
- **What:** Six items appear in both menus, declared twice in XAML with different icon markup, and
  kept in sync at runtime by `OnMenuOpening` iterating explicit arrays of paired controls
  (`[TrayStartWithWindows, WindowStartWithWindows]` and two more). The tray menu additionally carries
  "Show workspaces", which the window menu does not and should not.
- **Why it matters:** Mostly a maintenance cost (structural cause: the same duplication UIP-10 finds
  between the two windows), but it has a product edge: the next item added will be added to one menu,
  and a person who learned it from the tray icon will not find it in the window. The runtime sync
  code is already the shape that only works while somebody remembers to extend the arrays.
- **Suggested change:** One `MenuFlyout` built once in code or declared once as a resource and
  referenced twice, with "Show workspaces" conditionally prepended. Pattern: *one declaration, two
  attachment points*.

### USE-16 The wiki pitch and the shipped windows disagree about what the inspector is and about what the panel is for

- **Severity:** Low
- **Effort:** S
- **Where:** wiki *The inspector* and *The Rose panel*; `src/RoseMcp.Inspector/MainWindow.xaml.cs:17-25`
- **What:** The inspector page positions the window for "the person running the broker who needs
  visibility across debugging sessions". `MainWindow`'s class summary states the opposite as a
  deliberate decision: "There is deliberately no list of other sessions here: the tray is where
  sessions are listed, and a window that could be re-pointed at another process would make its own
  title a lie."
- **Why it matters:** A first-time reader arrives at the inspector expecting a console over their
  sessions and finds a single-process window with no session list and no explanation, which reads as
  a missing feature rather than a decision. The Rose panel page gives Magnify billing comparable to
  Select, which is defensible (USE-08 withdraws the claim that magnify is weak) but explains none of
  the filtering reason that makes it worth that billing.
- **Suggested change:** Rewrite the inspector page's opening around one process and one agent ("the
  window for watching an agent debug one app"), and point cross-session questions at the tray page.
  On the panel page, keep magnify's billing and give it the sentence it is missing: the Windows
  magnifier smooths pixels and cannot be told not to, so it can neither read a colour nor show a
  one-pixel gap (USE-08).

### USE-17 The tray spends 44px of a 400px minimum-height window on its own name

- **Severity:** Low
- **Effort:** S
- **Where:** `src/RoseMcp.Tray/MainWindow.xaml:94-99`, `MainWindow.xaml.cs:46-49`
- **What:** A 44px title bar containing a 18px mark and the caption "RoseMCP", all of it drag region.
  The word also appears in the taskbar, in the tray tooltip (`DescribeTooltip`), and 6px below in the
  headline's context. The inspector's equivalent is 40px and earns it, because its title names the
  process being debugged and that is how a person picks between two inspector windows.
- **Why it matters:** Small, but it is the top 11% of the minimum window and it is the only element
  in the tray that shows nothing. Everything below it is information.
- **Suggested change:** Shrink to 32px and put the endpoint or the state summary in it, or make the
  headline row itself the drag region (`SetTitleBar` accepts any element). Either way the space goes
  to something a reader acts on.

---

## What to cut

The owner asked for permission to hear this, so here it is without hedging. Everything below earns
less than it costs, in maintenance, in screen space, or in risk to somebody's running application.

| Cut | Where | Size | Why it earns less than it costs |
|---|---|---|---|
| ~~The pixel lens and the hex colour readout~~ **Withdrawn.** | `tap_tool_zoom.h` | -- | The first draft cut this as an OS duplicate. It is not one: Windows Magnifier filters bilinearly with no way to disable it, so it cannot read an exact colour or show a one-pixel gap at a corner radius. Keep the feature; write down why it exists (USE-08). |
| **The Threads pane** | `ThreadsPane.xaml` + `.xaml.cs` + `ThreadRow` | ~245 lines | A strict subset of Visual Studio's Threads window, answering no supervision question, and the only pane that freezes somebody's application as a side effect of being on screen. |
| **The Breakpoints compose form's position** (not the form) | `BreakpointsPane.xaml:17-150` | reorder only | Keep the flow -- it is good -- but it is a driving feature sitting where the supervision view should be. Demote to a disclosure. |
| **The tray's 44px title bar** | `MainWindow.xaml:94-99` | 6 lines | The only element in the tray that shows nothing, occupying 11% of the minimum window. |
| **The second copy of the tray menu** | `MainWindow.xaml:121-159` | ~40 lines | Two hand-maintained copies already differ; `OnMenuOpening` syncs them by iterating named pairs. |
| **`loaded in 24.3s` in the permanent facts line** | `WorkspaceRow.DescribeFacts` | 1 line | Actionable once, noise for the rest of the worker's life. Belongs in the on-demand detail (USE-01), not on the card forever. |

Two things I considered cutting and would keep:

- **App scaling** (`Scale the app itself`, ~80 lines of `tap_tool_zoom.h`), kept for the same reason as
  the lens. It scales the live visual tree rather than the framebuffer, so text and vectors stay
  sharp under magnification, which no framebuffer magnifier can do at any filter setting. The three
  zoom modes answer three different questions and none substitutes for another.
- **`MemoryDetail`** (the managed heap beside the working set). The gap between the two is mostly
  compilation cache, which is the number that decides whether closing a workspace frees anything
  real. It earns its two lines.

---

## Redesign proposals, smallest change first

### The tray -- three changes, none of them structural

1. **Unfold the health InfoBar.** Add a "Details" disclosure that fetches a real
   `WorkspaceStatusReport` on demand and lists the failed projects by name with their per-project
   status, then the analyzer load failures verbatim. The minimum version is one line:
   `DescribeFacts` shows the names in `FailedProjects` instead of their count. (USE-01)
2. **Promote the activity list.** Raise `RecentPerWorkspace` from 8 to a few hundred, add
   `CallOrigin.Directory` to `WorkerActivity`, and give the window a single "Activity" view across
   all workspaces -- newest first, filterable to failures -- reached from the header. The per-card
   expander can stay as it is; the point is that there is somewhere to go from it. (USE-04, USE-05)
3. **Say when the report is stale.** `InfoAge` beside the heartbeat, silent under a threshold.
   `Notice` into an Informational InfoBar on the session card. (USE-03)

That is the whole tray redesign. It is close enough to its job that nothing else is needed.

### The in-app panel -- explain one feature, add one button

1. Write down why the pixel lens exists, in the header, a decision record and the tooltip (USE-08).
2. Then **#226**: a button that opens the inspector on this session, by the
   four-hop route the issue already designs (named event, host latch, existing once-a-second
   self-report, `InspectorLauncher`). That single button turns a selection into an answer and is the
   only thing on this toolbar that changes the product's core latency.
3. Then, in order: flash the element an agent's `rose_xaml_apply` just changed (the overlay already
   draws an outline on a handle); pin a rulers measurement and expose it through the selection
   channel so the agent can read it (USE-14).

### The inspector -- rebuilt from its job description

This is the surface furthest from its job, so this is the sketch the brief asked for. The job:
**a human watching an agent debug their app.** Not a debugger. Four questions, in order: what is it
doing, why did it stop, do I agree, let me take over or let it go.

```
+---------------------------------------------------------------------------------------+
| (o) SampleUwpApp -- watched by Claude Code (sb/widget-fix)     [ - ] [ [] ] [ X ]      |
+---------------------------------------------------------------------------------------+
|  STOPPED at breakpoint bp-3  .  Widget.Refresh, Widget.cs:41  .  thread 7             |  <- WHY
|  Held for you, 1m 47s left   [ Keep holding ]  [ Let it go ]  [ Step over ]           |  <- WHAT NOW
+---------------------------------------------------------------------------------------+
|  THE AGENT                                                       host answered 0.4s ago|  <- WHAT IT
|   > rose_debug_evaluate      items.Count                              0.2s   ok        |     IS DOING
|     rose_debug_set_breakpoint Widget.Refresh:41                       0.4s   ok        |
|     rose_xaml_apply  /Grid[0]/Border[2]  Margin 8,4,8,4 -> 16,4,16,4  1.1s   ok        |
|     rose_debug_attach        pid 9876                                 2.3s   ok        |
|                                                          [ all 214 calls ]             |
+---------------------------------------------------------------------------------------+
|  Why it stopped | The app | Breakpoints | Everything that happened                      |
+---------------------------------------------------------------------------------------+
|  Widget.Refresh, Widget.cs:41                          [ Copy this for the agent ]     |
|    39    var items = _store.All();                                                     |
|    40    if (items.Count == 0) return;                                                 |
|    41    Render(items);                                 <- stopped here                |
|                                                                                        |
|    items   Count = 14   List<Widget>   >                                               |
|    index   3            Int32                                                          |
|                                                                                        |
|    called from  Page.OnLoaded Page.cs:88  >  3 native frames  >  Program.Main           |
+---------------------------------------------------------------------------------------+
```

What changed, and why:

- **The agent's call stream is permanent chrome, not a tab.** It is the subject of the window. It is
  also the only thing here Visual Studio structurally cannot show. (USE-02)
- **"Why it stopped" replaces "Stack".** Same material, laid out as an answer: the line first, the
  locals under it, the call chain as a single readable sentence rather than a 320px list. A
  supervisor asks "is this surprising", not "walk me up the frames"; the frames become a
  breadcrumb that expands when they matter.
- **Threads is gone**, its one useful fact folded into the header line. (USE-07)
- **Breakpoints becomes a read view**, with the compose form behind an Add button. (USE-06)
- **Events becomes "Everything that happened"** -- unchanged, and still the second-best pane.
- **The XAML tab becomes "The app"**, and stays alive during a stop with a caption saying how old the
  tree is. (USE-12, #225)
- **The hold stops being a caption and becomes the second line of the window.** "Held for you, 1m 47s
  left" with two buttons is the supervision interaction; `Pause / Continue / Step in / Step over /
  Step out` is a debugger toolbar. Keep the steps -- a supervisor does step occasionally -- but one,
  not three, with the rest behind a chevron.
- **Every fact has a copy route**, and the stop has a purpose-built one. (USE-09)
- **The title says who is driving.** If several agents can hold sessions, the window should say which.

Effort: the panes already exist and `RoseMcp.Ui.Core` already holds the rows. The agent strip is one
`ItemsRepeater` over data already in `_summary`. The reshuffle is layout. This is a week, not a
rewrite -- and it turns a small VS clone into something VS cannot be.

---

## The agent-supervision thesis

**RoseMCP's user interfaces should be aimed at a human supervising an agent, not at a human doing the
work. Commit to it explicitly, in writing, as a decision record.**

The argument in four steps:

1. **Rose cannot win the driving job and should stop entering it.** Visual Studio has twenty-five
   years of debugger UI. The inspector's Stack, Threads and Breakpoints panes are each a strict
   subset of a VS window, and always will be, because every hour spent closing that gap is an hour
   not spent on something VS does not have. Three of five tabs are currently in a race that cannot
   be won.

2. **Rose has a monopoly on a set of facts VS cannot see.** What tool the agent called, with what
   argument, how long it took, whether it failed, what it applied, what element the human picked,
   which solution answered and at what revision. `ActivityLog`, `WorkerActivity`,
   `LiveAppSessionSummary` and `WorkspaceStatusReport` compute all of it today. Visual Studio, git,
   the file watcher and the terminal each see the *effect*; only Rose saw the *call*. That is the
   defensible product, and almost none of it is on screen.

3. **The one thing the UI layer invented has no analogue anywhere else, and it is a supervision
   primitive.** `HoldKeeper` exists so a person can keep an application still *while somebody else
   is driving it*. Visual Studio has no concept of this because in Visual Studio the person at the
   keyboard is the debugger. It is the most product-distinctive code in `RoseMcp.Ui.Core` and it is
   currently surfaced as a caption on the right-hand end of a toolbar.

4. **The repository's own method already assumes it.** "Dogfooding is the point" puts a human beside
   an agent working on this code all day. That human never drives; they watch, and they intervene.
   The UI should be built for the person the development method already creates.

**What changes if you commit:**

- The inspector's spine becomes the agent's call stream (the sketch above). Three tabs become
  supporting evidence rather than the point.
- The tray's job statement changes from "what is loaded" to "what are my agents doing to my code",
  which makes the activity log a feature rather than debug output, and makes attribution (USE-05)
  mandatory rather than nice.
- **Copyability becomes a requirement, not a polish item.** The human's only channel to the agent is
  text. A supervision UI where nothing can be copied is a supervision UI with no output.
- **Approval becomes a coherent future feature.** "Do I agree with what it is about to do" is
  unanswerable today because nothing in the product has a pending action. If the UIs are for
  supervisors, a hold-before-write -- an agent's `rose_xaml_apply` or `rose_rename_symbol` pausing
  for a person who has the window open -- becomes the natural next thing to build, and `HoldKeeper`
  is already the shape of it. That is a feature nobody else can ship.
- **Feature triage gets a test.** "Would a supervisor use this?" kills the threads
  pane and the breakpoint compose form's prominence, spares the pixel lens (a supervisor checking an
  agent's layout edit is exactly who needs an unfiltered pixel), and promotes #222-#226 -- all XAML
  tree usability, all filed by somebody looking rather than driving -- to the top. The issues users
  actually filed are already supervision issues.

The honest counter-argument, stated so it can be weighed: the breakpoint compose flow and the method
search are genuinely good, and they exist because an agentic session has no IDE open beside it --
which is a real configuration and one the decision record names explicitly. If that is the intended
setup, some driving capability has to stay. The resolution is not to delete it but to stop giving it
the prime position: a supervisor who occasionally drives needs the driving tools one click away, not
in the top half of the pane.

---

## Pit-of-success inversions

| Rule today | Mechanism |
|---|---|
| ~~A contract property written for a window is remembered into a window by whoever adds it.~~ **#295.** | A word match over the windows: it cannot tell rendering from a stray mention, and still catches a property nobody named anywhere. |
| `WorkspaceSummary` is a hand-maintained lossy projection of `WorkspaceStatusReport`, and a field added to the report is silently invisible to every window. | A test that fails when the report gains a property with no counterpart on the summary and no entry in a `SummarisedElsewhere` list. Forces the "does a person need this" decision at the moment the field is added, which is the only moment anybody knows. |
| Attribution on an activity would be remembered by each call site. | `ActivityLog.Begin` reads `CallOrigin.Directory` and `CallSession.Id` itself -- they are `AsyncLocal` and already ambient -- so no call site can forget. Exactly the argument `WorkspaceScopedResult` already makes for results. |
| A pane takes a hold on somebody's application as a side effect of becoming visible, and whether that is justified is a judgement per pane (see also UIP-08). | Make the hold an explicit window-level state with one visible control and one owner. A pane declares "I can only answer while held" and the *window* decides, so adding a pane cannot silently freeze a user's app. |
| Facts are rendered into bare `TextBlock`s, so copyability is per-element and mostly absent. | One `FactTextBlockStyle` in `RoseMcp.Ui/Themes/Rose.xaml` with `IsTextSelectionEnabled="True"`, and a convention that anything carrying a fact uses it. The styled resource is the only spelling, so the default is selectable. |
| Two menus and two windows keep their shared parts in sync by hand (`OnMenuOpening` iterating named pairs; UIP-10 finds the same shape across windows). | Declare the shared flyout once as a resource in `RoseMcp.Ui` and attach it twice. One declaration, two attachment points. |
| An empty state is written inline in XAML in the tray and in one testable file in the inspector. | Move the tray's to an `TrayText` beside `InspectorText`. The file that exists is the better pattern and it is already proven. |

---

## Open questions for Steve

1. ~~Who is the pixel lens for?~~ **Answered in USE-08.** What is left is narrower: **is anything else
   in the product justified by a platform limitation that is not written down?** The magnifier came
   within one review of being deleted because its reason lived only in the author's head.
2. **Is the inspector meant to be used instead of Visual Studio, or beside it?** The breakpoint
   compose flow assumes instead (no IDE open, so a method search is the only way to name a location);
   the read-only, non-navigable source viewer assumes beside. Both are defensible, but the window
   currently assumes both at once, and that is why the Stack pane feels thin.
3. **Is there arbitration between a human and an agent driving one session?** If a person presses
   Continue while the agent is mid-step, or the agent continues while a person is reading a held
   stop, what is supposed to happen? `HoldKeeper` handles the mechanics of one hold; nothing appears
   to handle the intent conflict, and a supervision UI eventually has to.
4. **Is `LiveAppSessionSummary` deliberately workspace-less, or has it just not been needed?** Adding
   the key looks cheap on the attach and launch paths and would connect the tray's two halves
   (USE-11).
5. **Should `ShowInspectorOnAttach` default to on for a XAML target?** It is the single setting that
   decides whether the inspector is a supervision window that is simply there, or a thing you go and
   find after the interesting moment has passed.
6. **Is the tray ever run by somebody other than the developer whose machine it is?** #160 hints at a
   per-user future. If the answer is no, the activity log can be much more generous than it is; if
   yes, USE-05's attribution has a second reason to exist.
7. **What is the intended relationship between the activity log and the Serilog files?** Today three
   of four dead ends in the tray end at "open the log folder". If the activity log is meant to
   eventually answer those, it needs to hold far more; if the logs are meant to, the window needs to
   open the *file*, not the folder.

---

## Rose dogfooding notes

**Tools reached for: `rose_find_references` (three calls). Nothing else.**

**Where it won, clearly.** USE-03 claims three `LiveAppSessionSummary` properties are computed and
rendered by no window. `rose_find_references` on each, with `includePreviews=false`, answered it
better than grep did:

```
InfoAge          6 refs -- LiveAppSession.Describe (x2), 4 in test projects.  No UI project.
Notice           2 refs -- LiveAppSession.Describe, LiveAppSession.Note.      No reader at all,
                                                                              not even a test.
InstallLocation  4 refs -- LiveAppSession.Describe, 3 in LiveAppUwpTests.     No UI project.
```

This is the tool working exactly as designed and beating the alternative on the thing that mattered.
I had already grepped for the same names and got a file list; what turned that into a finding was
`project` and `isTestProject` on every hit, which let me say "written by the broker, asserted by a
test, read by nobody" without opening a file. Grep cannot compute that at all -- it would have needed
a second pass mapping paths to projects. It also correctly excluded the `RoseMcp.Contracts.xml`
documentation matches and the `.dll` binary match that polluted my grep output for `InstallLocation`.
**Worth recording as a win, because most dogfooding notes in this review series are losses.**

**One defect in that answer.** Each result's `definitions` array lists the same declaration three or
four times at different columns on one line -- for `InfoAge`, line 96 at columns 19, 19, 29 and 34.
One property, one declaration, four entries, one of them an exact duplicate. It is harmless to read
and it inflates the payload of the cheapest possible query, and a caller counting definitions gets a
wrong answer. AGT territory.

**Where I did not reach for Rose, and why.**

- **Reading XAML as a layout** -- the central activity of this review -- has no Rose tool at all.
  XAML files are `AdditionalDocument`s in the workspace, `ProjectStatus.XamlMarkupCount` counts them,
  and `XamlStubbedCount` reports how many classes got a synthesised partial, so the worker knows a
  great deal about them. Nothing exposes any of it. Every `.xaml` here was read with `Read`. This is
  a legitimate gap rather than a defect -- Roslyn does not parse markup -- but it is worth naming,
  because "what does this window look like" is a question asked constantly in a repository with three
  WinUI projects, and the XAML stub generator means Rose is closer to answering it than it looks.
- **The C++ tap (6,794 lines across 14 headers)** is correctly outside Rose's remit. `grep` and
  `Read`, with no complaint.
- **"Which public members of this type are read by nobody"** is the query this review actually wanted,
  asked about roughly twenty properties at once. `rose_find_references` is one symbol per call, so
  twenty calls; grep over `src/` answered all twenty in one. I used grep first for that reason and
  came back to Rose only to verify the three that became a finding. **The missing tool is a
  negative-space query**: "find members of this type with no references outside their declaring
  assembly", or simply a `symbols` array on `rose_find_references`. That is a natural agent question
  -- dead code, unused DTO fields, an interface nobody implements -- and it is exactly the kind of
  thing only a semantic model can answer, so losing it to grep is the worst kind of loss.
- **`rose_workspace_status`** was not run: the review's own ground truth already records that this
  repository reports Degraded, with the two WinUI projects logging "Cannot resolve Assembly or
  Windows Metadata file ... RoseMcp.Contracts.dll" during the design-time build. Worth noting what
  that means for the three calls above: they were made against a solution whose WinUI projects --
  `RoseMcp.Tray` and `RoseMcp.Inspector`, the two subjects of this review -- are the ones with
  resolution failures. The results were consistent with grep, so I trusted them. But it is a small,
  real instance of USE-01 experienced from the inside: I had to go to a markdown file written by
  somebody else to learn that the workspace answering my questions might not be trustworthy, and the
  window built to tell me that could only have said "Answers may be incomplete".
- **`rose_outline` and `rose_symbol_info`** were not reached for. The C# in scope is mostly
  code-behind and row classes of 100-400 lines, and reading one whole is both cheaper and necessary:
  the review is about what the file *shows a user*, which is a property of the whole file rather than
  of a symbol. No complaint -- this is a case where the tool is genuinely not the right shape for the
  question, rather than one where it lost.
