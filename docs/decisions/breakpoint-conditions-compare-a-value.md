# A breakpoint condition compares one value with a literal

**Decision.** A condition on a breakpoint or a tracepoint is `name OP literal`, where OP is one of
`==`, `!=`, `<`, `<=`, `>` and `>=`, evaluated on each hit against the top frame's arguments and
locals. Both sides compare as numbers when both parse as numbers, as booleans for `true` and `false`,
and as strings otherwise. A condition naming a variable the frame does not have does not fire.

**Why not expressions.** A condition that calls a method, reads a property or walks `this.X.Y` needs
func-eval, which runs the target's own code on a stopped thread. That can hang the target or change
its state, and a condition runs on every hit of the method it guards, which multiplies the risk by how
hot the method is. See
[expression evaluation](expression-evaluation-reads-memory-and-runs-no-code.md). The common case --
stop when `id == 42` -- needs none of it.

**Why a missing variable does not fire.** A variable goes in and out of scope within one method. A
condition that errored at every hit where its variable was not in scope would bury the hits the caller
asked about under failures about ones it did not.
