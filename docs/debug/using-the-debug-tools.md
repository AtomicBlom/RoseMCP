# Debugging a live app with RoseMCP

RoseMCP can attach a debugger to a running .NET process, watch it, set tracepoints and breakpoints,
step, and read a stopped frame -- all without Visual Studio and without loading the solution's Roslyn
view a second time. That last point is the reason it exists: an external debugger doubles the memory
an agentic session costs, and this does not.

## Getting an agent to use it

The server sends debugging guidance during `initialize` (see `ServiceCollectionExtensions.Instructions`),
so the model reads it before choosing an approach. To reinforce it in a consuming repository, add to
that project's `CLAUDE.md`:

```markdown
## Debugging a running .NET app

Use the Roslyn-backed `rose_debug_*` MCP tools to debug a live .NET process rather than adding
print statements and rebuilding, or attaching an external debugger (which loads the solution a
second time). `rose_debug_attach` by pid or `rose_debug_launch` to start one under the debugger;
`rose_debug_events` for exceptions (with stack traces), logs, and breakpoint hits; a
`rose_debug_add_tracepoint` to log a method's hits without pausing, or `rose_debug_set_breakpoint`
to hold it and read its stack and locals, then `rose_debug_continue` / `rose_debug_step`.
```

## The surface

| Tool | What it does |
| --- | --- |
| `rose_debug_attach` | Attach to a running process by pid. Local, same-user only. |
| `rose_debug_launch` | Launch a local .NET executable under the debugger, from startup. |
| `rose_debug_launch_uwp` | Activate a packaged (UWP) app under the debugger by AUMID, from birth -- startup, first OnLaunched, and all -- through an architecture-matched host. |
| `rose_debug_events` | Read events since a cursor: exceptions (with stacks), logs, module loads, hits. |
| `rose_debug_add_tracepoint` | Log a method's hits and keep running (with an optional condition / hit-count filter). |
| `rose_debug_set_breakpoint` | Hold the target on hit and record its stack and top-frame locals (with an optional condition). |
| `rose_debug_continue` / `rose_debug_step` | Resume a held target, or step in / over / out. |
| `rose_debug_list_tracepoints` / `_list_breakpoints` / `_remove_*` | Inspect and remove what is set. |
| `rose_debug_detach` / `rose_debug_list` | End a session (leaving the target running); list sessions. |
| `rose_xaml_tree` | Read a live XAML app's visual tree: a flat element list (handle, parent, child index, type, x:Name) that rebuilds into a tree; can be rooted at a named element and paged. |
| `rose_xaml_properties` | Read one element's properties (by handle) with provenance (Local / Style / Inherited / Default …) and, when the app carries source info, the XAML file and line that set each. |
| `rose_xaml_apply` | Live edit: apply what a XAML file now holds to the running app's visual tree with no relaunch, reporting each edit's outcome. Properties, added and removed elements, attached properties and keyed resources, on named elements and unnamed ones alike. Visual Studio calls this XAML Hot Reload. |
| `rose_debug_evaluate` | While stopped, evaluate a field-access expression (`name`, `name.field.field`) against the frame — read directly from memory, no debuggee code run. |
| `rose_xaml_select_mode` / `rose_xaml_selection` | Let the user point: arm select mode, their next click picks that element, and the selection (type, `x:Name`, handle) feeds the property and live-edit tools. They can also arm it themselves from the in-app toolbar, so read the selection before arming. |

## The in-app toolbar

The first XAML tool to run against a session puts a small RoseMCP toolbar on the app's diagnostics UI
layer, and leaves it there. It exists so a person can point at something without the agent having to
arm anything first: press **Select Element**, click the thing you mean, then say "look at the element I
selected" -- the agent reads it with `rose_xaml_selection`, and the handle it gets back feeds
`rose_xaml_properties` and `rose_xaml_apply` directly.

- **The app stays usable.** The overlay carries no background of its own, and a XAML panel with no
  background takes no part in hit testing, so clicks pass straight through to the app. Only the toolbar
  itself takes input. There is no modifier chord to collide with anything the app already uses.
- **Select mode is visible.** While it is armed the window carries a faint tint, the mode button wears
  RoseMCP's accent, and the element under the pointer is outlined with a badge naming it. The click
  that picks one is swallowed rather than also reaching the app. **Idle** cancels.
- **The pick leaves a mark.** The chosen element keeps a solid outline and badge after select mode
  ends, so "the selected element" means the same thing to you and to the agent until the next pick.
- **It picks what you clicked.** The framework's own hit test decides, so an invisible panel stretched
  over the window -- an empty dialog host, say -- cannot swallow every click. And **Just my XAML** (the
  `{ }` toggle, on by default) prefers the element your markup declares over a control template's
  internals, so clicking a button selects the button rather than some `TextBlock` inside it. The
  agent gets the whole stack under the click either way, so it can walk up or down without asking again.
- **It moves and folds away.** Drag it by the grip; **Hide** collapses it to that grip alone, and a tap
  on the grip brings it back. It stays inside the window however hard you throw it.
- Either side can arm select mode, and both are the same act -- the mode is read back from the toolbar
  rather than assumed, so an agent can tell whether a person has armed or cancelled it.
- A selection sticks until the next pick is armed, so reading the tree or some properties in between
  does not lose it.

## Live-editing a running app

`rose_xaml_apply` takes a **file path**, and the loop is edit, apply, edit, apply:

```
rose_xaml_apply  sessionId=…  filePath=D:/app/MainPage.xaml
```

Each call diffs what the file holds now against what the session last sent to that app, so nothing has
to be carried between calls -- which matters because a caller that has just written a file no longer
holds what was in it. The diff reduces to the smallest set of edits, each one is applied to the live
tree, and each comes back with its own outcome.

**The first call for a file records a baseline and applies nothing.** What the running app was built
from is not on disk any more once you have edited it, and this side will not diff the file against
itself and call the empty result success -- that would skip your first edit silently. So either call it
once before you start editing, or pass `oldXaml` alongside `filePath` for that first apply. Every call
after it needs only the path.

Two more things worth knowing about the loop:

- **An edit reported as failed is not retried.** The baseline moves whether or not every edit took,
  because re-sending one that adds an element would add a second copy of it on the attempt that works.
  Read the results; a failure is yours to act on.
- **Read the notes as well as the results.** They carry what the diff worked out but does not apply,
  and the difference between "nothing changed" and "the diff found nothing to do", which both come out
  as zero edits and mean different things.

For markup with no file behind it -- something composed rather than saved -- pass `oldXaml` and
`newXaml` instead of `filePath`.

## Limits worth knowing

- Line-granular stepping and local names need a PDB; without one a step lands at the runtime's own
  step boundaries and locals are indexed rather than named.
- Expression evaluation reads field-access chains only (`rose_debug_evaluate`): an argument or local,
  then `.field` into the object graph, read from memory. It runs none of the debuggee's own code, so
  property getters, method calls, and an object's `ToString` are deliberately not evaluated — that
  needs func-eval, which can hang or corrupt the target, and is left to an external debugger.
- Conditions are cheap value-compares (`name OP literal`) over the stopped frame, not full expressions.
- A live edit changes the running app's tree, never its files or its markup. Nothing is written back,
  so the file remains the only record of the change -- and this is why "live edit" rather than "hot
  reload": nothing is reloaded. Every edit lands on the element objects that exist at that moment.
  A relaunch loses them, and so does anything that rebuilds that part of the UI, because the new
  objects come from the app's compiled markup, which still says what it always did. Navigating away
  from an uncached page and back is enough. (That follows from the mechanism -- property sets against
  live instances -- rather than from a test: the probe app is a single page with no navigation.)
- An element created live cannot be given an `x:Name`. Names are registered when the markup is
  parsed, so the tree gets the element but nothing can address it by name afterwards; the apply says
  so in its notes, and the element is still reachable by its `Type[index]` address.
