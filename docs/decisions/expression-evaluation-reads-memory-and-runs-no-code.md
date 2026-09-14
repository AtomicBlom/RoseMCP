# Expression evaluation reads memory and runs none of the target's code

**Decision.** `rose_debug_evaluate` evaluates a path -- an argument or local by name or slot, then
`.field` and `[index]` -- by reading values straight from memory at a stop. It runs no property getter,
no method and no `ToString`. Func-eval through `ICorDebugEval` is deliberately not offered.

**Why.** Func-eval runs the target's own code on the stopped thread. That code can block on a lock
another frozen thread holds and hang the target; it can change the target's state, so that what is
being inspected is not what stopped; and it re-enters the debugger through a callback of its own. A
tool whose purpose is to look must not be able to do any of that. Inspecting an object graph that a
hostile or broken program built must not run that program.

**What it covers.** The usual need at a stop is to drill into an object graph --
`request.Headers.Count`, `items[3].Name` -- and memory reads answer it. Fields declared on base classes
resolve as well as the type's own, because a value's expansion lists them, and a path an expansion hands
out has to resolve when it is handed back.

**What it leaves to other tools.** A computed property, a method's result or an object's string form
needs a real debugger. That is a trade made knowingly, and an agent that needs them can attach Visual
Studio.
