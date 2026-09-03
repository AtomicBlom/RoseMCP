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
| `rose_xaml_apply` | Hot-reload: diff two XAML versions and apply the changes to the live tree with no relaunch, reporting each edit's outcome. Property changes on named elements today. |
| `rose_debug_evaluate` | While stopped, evaluate a field-access expression (`name`, `name.field.field`) against the frame — read directly from memory, no debuggee code run. |

## Limits worth knowing

- Line-granular stepping and local names need a PDB; without one a step lands at the runtime's own
  step boundaries and locals are indexed rather than named.
- Expression evaluation reads field-access chains only (`rose_debug_evaluate`): an argument or local,
  then `.field` into the object graph, read from memory. It runs none of the debuggee's own code, so
  property getters, method calls, and an object's `ToString` are deliberately not evaluated — that
  needs func-eval, which can hang or corrupt the target, and is left to an external debugger.
- Conditions are cheap value-compares (`name OP literal`) over the stopped frame, not full expressions.
