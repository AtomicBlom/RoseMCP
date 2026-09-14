# A pick is what a click would hit, and prefers the app's own markup

**Decision.** Picking an element asks XAML's hit test with `includeAllElements` false, so the element
picked is the one the app's input system would route that click to. With **Just my XAML** on, which is
the default, the pick prefers an element the app's own markup declares over a control template's
internals. Every pick also returns the ordered stack of elements under the click.

**Why honour hit testing.** With every element included, an empty full-window `Grid` with no background
-- an idle dialog host, say -- sits over everything, and every click resolves to it while the app
underneath works perfectly well. "Click an element to select it" has to mean the element the click
would reach, or it means nothing. Including everything stays available as an explicit choice, because
inspecting an invisible host is occasionally the point.

**Why Just my XAML is exact rather than a heuristic.** Every element the tree reports carries source
info. The app's own markup resolves to an `ms-appx:///` URI with a real line, and a template's parts
resolve to `ms-resource:///` theme files. So the filter compares a URI scheme instead of guessing from
names or namespaces.

**Why it falls back.** An app without source info has nothing to compare. Treating absent source info as
"framework" would empty the filter on exactly those apps, so absent is not framework, and when nothing
under a click came from the app, the framework's own pick stands.

**Why the whole stack.** A click on a button lands on some part of its template, and a click meant for a
container lands on its content. The wanted element is usually a step away, and the stack saves asking
the person to click again.
