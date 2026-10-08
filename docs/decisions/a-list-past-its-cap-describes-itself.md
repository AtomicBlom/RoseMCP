# A list past its cap describes itself, and every group it names is a filter

**Decision.** Every list on the read surface that a cap can cut answers past the cap with the shape of
what it found: counts along each facet its items carry, most first, each group keyed by the value the
narrowing argument of the same name takes. `rose_find_references` did first; `rose_diagnostics`,
`rose_search_symbols` and `rose_debug_events` do the same.

- `rose_diagnostics` past `maxResults` lists nothing and gives its shape: by `id` (with its
  severity), by `project`, and by `filePath` for the ten files holding most, with how many files there
  are and how many diagnostics are in generated code. `id` and `isGenerated` are its filters for the
  two facets nothing selected on before; `project` and `filePath` already narrowed the scope.
- `rose_search_symbols` past `maxResults` still lists the closest matches, and adds the shape of
  every match by `kind` and `project`, which are its filters.
- `rose_debug_events` still gives its page, and where the page stopped at its limit with matching
  events buffered past it, says how those divide in `beyond`: by kind, which `kinds` selects, and by
  exception type, which `exceptionType` selects.

**The failure it prevents.** A list cut at its cap reads as the whole answer. The first two hundred
diagnostics of nine hundred are whichever sort first, and a caller fixing them fixes a sample; the
first fifty matches of a search hide the one the caller wanted under a kind it did not ask for; a
page of five hundred module loads hides the one exception after it. In each case the only ways on
were to read everything or to guess at a narrower question, and the answer had the facts to say which
narrower question had an answer.

**Why search and events keep their list.** A reference search's first two hundred in path order are an
arbitrary sample, so the list gives way to the shape. A search's matches are ordered by how close
each name is to what was typed, so its first matches are the best part of the answer, not an accident
of where they sit, and the shape is added to them rather than put in their place. An event page is a
stream read in order: its first events are the next ones, and the cursor reaches the rest, so the
page is not a sample either -- what it lacked was any sign of what lay past it.

**Why diagnostics give up their list.** Which diagnostics sort first is severity and path order, which
says nothing about which matter. A caller with a broken build needs to know the build is broken in
three places by one missing type, and the shape says that in a few lines, where two hundred CS0246s
in alphabetical file order do not.

**Why not every facet of an event.** An event also carries its thread and, for a module load, the
module. Neither is grouped, and neither has a filter: a thread is named so the frame tools can read
it, and what a caller narrows a page by is what happened rather than where; a module name is carried
only by a module load, one per module, which `kinds` already selects. Each is recorded in
`ProducedFactTests` with that reason, which is where a facet with no filter has to say why.

**Why the grouping is written twice.** The worker's three shapes share `FacetGroups`, so the ordering
each depends on is written once there. The live-app host cannot reference the worker, and the one
assembly both reference holds logic only by an explicit list, so the host groups its events with the
same order in a few lines of its own rather than widening that list for a group-by.
