# It is a live edit, not a hot reload

**Decision.** The capability behind `rose_xaml_apply` is called a **live edit**, the operation an
**apply**, and the smallest unit an **edit** -- as in `XamlEdit` and `LiveXamlEditResult`. The tool's
description and the server instructions each say "what Visual Studio calls XAML Hot Reload" once, as a
search term.

**Why not "hot reload".** "Reload" says the app's markup is now the new markup, and it is not. Every edit
is a property set or an `AddChild` against the element objects that exist at that instant, and the
compiled markup is untouched. Anything that rebuilds that part of the interface -- a relaunch, or
navigating away from an uncached page and back -- gets the original. A reader who took "hot reload" at
face value would expect elements created later to carry the change, and they do not.

**Why not "reload" for a second reason.** It is taken. `rose_workspace_reload` genuinely reloads a
solution, and one word meaning both would carry no information.

**Why mention Hot Reload at all.** It is Microsoft's name for the same mechanism, running through the
same diagnostics channel. An agent that knows the Visual Studio feature should recognise this as the
thing it wants, and the instructions are read before an approach is chosen.
