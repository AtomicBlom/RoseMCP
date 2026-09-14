# Removals among siblings are applied last-first

**Decision.** When a live edit removes several children of one parent, `XamlDiff` emits the removals in
reverse document order, so the last sibling goes first.

**Why.** An unnamed element is addressed by its position among its siblings, and the apply resolves that
position against the live collection at the moment each edit runs -- deliberately, because an add earlier
in the same batch has already shifted everything after it. A removal shifts things too, in the other
direction. In document order, removing two adjacent children removes the first and then finds no second,
because the second has moved into the first one's place. Last-first, nothing a later edit names can move
under it.

**Why the unit test pins the order rather than the outcome.** The ordering is the whole of the fix, and
an outcome test would pass by accident whenever the removed elements happened not to be adjacent.

**How a bug like this hides.** A fixture that empties test slots without checking what the apply reported
hands a slot that held two elements to the next test holding one. Slots come off a stack, so the very
next test picks up the residue and fails on a count it never caused. A removal reported as applied that
did not happen, a count off by one, and a test that passes alone and fails in company are then one bug
seen from three places. So the fixture checks its own cleanup, from the statuses the apply returns.
