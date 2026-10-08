# Accessibility is part of a signature change

**Decision.** `rose_change_signature` takes an `accessibility` argument beside `parameters`, and
either may be given alone. With accessibility alone it reaches any member or type, not only something
with a parameter list. Only the accessibility keywords are rewritten; every other modifier stays
where it is.

**Why a change of accessibility needed a tool.** It is a one-word edit that no tool could make on
its own. `rose_replace_member` made it correctly, but only when handed the whole member back, so a
fifty-line method cost fifty lines to change one keyword. Making nine members of four types public
for the unit suite was nine of those, and it was done with a text replace. Accessibility is also the
modifier a refactor moves most: it widens for a test or a sibling project and narrows after a split.

**Why on this tool rather than a modifier tool of its own.** #330 proposed a tool in the shape of
`rose_set_attribute` that adds, removes or replaces any modifier. Accessibility is the modifier that
has a group, and the group is the one this tool already works out. An override has to keep the
accessibility of what it overrides, or the build fails with CS0507, so the base all the way up and
every override all the way down have to change together. Parameters have to move together for the
same reason. A separate tool would have needed the same search over the same chain, and the same
whole-solution compile afterwards, because narrowing a member breaks code in other projects. Putting
both on one tool also lets a member gain a parameter and the visibility to be called with it in one
call and one compile.

The other modifiers have no such group. `static`, `sealed`, `virtual` and `async` change what the
member is, not who can see it, and each comes with a different set of things that break. They are
left to `rose_replace_member` until there is a case for one of them in particular.

**Why some changes are refused before the file is touched.** Some accessibility changes compile to an
error on the declaration itself, which would point the caller at a line they never asked to change:

- `protected` in a struct or a static class (CS0666, CS1057).
- Anything but `public` or `internal` on a type that is not nested (CS1527).
- An override of a member that a referenced assembly declares (CS0507).

Two changes are worse, because they compile cleanly and leave the type broken somewhere else:

- **Narrowing an implicit interface implementation.** The member implements the interface only
  while it is public, so making it narrower stops it implementing anything (CS0737). The refusal
  names the interface member and points to the explicit implementation instead.
- **A `protected internal` override in another assembly.** It has to be written as `protected`,
  because the internal half does not reach across assemblies. It gets `protected`, and the result
  says so.

**Why the uses are left to the compile.** Narrowing a member breaks whatever can no longer see it.
That is a fact about other code, and the only reliable way to learn it is to compile the whole
solution, which this tool does already. Which error a use gets depends on what else is in scope: an
accessible overload changes the error from CS0122 to one about that overload. So the result lists
what the compile found and does not try to predict it.
