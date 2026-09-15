# The tap's tiers

Read before adding a file under `src/RoseMcp.Xaml.Tap/`, moving code between the files there, or
changing the include order in either provider's `.cpp`.

- **A file's tier is what it names, not what it does, and the include order is what checks it.** Four
  tiers, and each provider's `.cpp` includes them in order: `tap_channel.h` and `tap_measure.h` name
  nothing external; `tap_diagnostics.h`, `tap_surface.h`, `tap_tree.h`, `tap_properties.h`,
  `tap_edits.h` and `tap_object.h` name only xamlOM, which Windows.UI.Xaml and Microsoft.UI.Xaml
  declare verbatim identically; `tap_render.h` and `tap_overlay.h` name the seven projection aliases
  and are therefore compiled once per framework; the provider itself names the real framework and
  defines the aliases, the CLSID and the seams.
  <br>
  The first two groups are included **above** the alias block, so naming a projection in one of them
  does not merely offend a convention -- it fails to compile, with the alias undefined. That is the
  whole enforcement, and it is why the order in the `.cpp` is load-bearing rather than tidy. The same
  trick is what `tap_diagnostics.h` already relies on: `TreeNode` lives there rather than in the
  channel because holding an `InstanceHandle` next door fails to compile.
- **The repair for a tier violation is a seam, never a reordering.** A projection reaching into
  `tap_object.h` presents as an undefined alias, and there are two ways to make the error go away.
  Moving that include back below the alias block silences it and silently returns 1,800 lines of
  framework-independent code to being compiled per framework, with nothing in the diff saying so --
  the file still builds, both providers still work, and the boundary is gone. The other way is to add
  a declaration to `tap_surface.h` and put the body in `tap_render.h`, which keeps the boundary and
  costs a few lines.
  <br>
  So a change that moves `#include ".../tap_object.h"` or `#include ".../tap_surface.h"` below the
  aliases is reverting this split, whatever the commit message says.
- **Nothing in a `tap_surface.h` signature is a projected type.** That is the property the split
  rests on, and it is what to check when adding to it: a handle, a string, a bool, an
  `::IInspectable*` or an `std::` container may cross the boundary; an `xaml::` anything may not. The
  overlay is reached through `IRoseOverlay` for exactly this reason -- all fourteen of the methods the
  request dispatcher calls already spoke in handles, strings and bools, so the concrete type was
  supplying nothing but a compile-time dependency on a framework.
  <br>
  A seam that needs to hand a projected type across is a seam in the wrong place. Give it the handle
  and let the far side resolve it, which is what the four reads in `tap_render.h` do: a handle goes
  in, a `try_as<>` chain finds the type that declares the property, and a string or another handle
  comes back.
- **Tier purity is not currently checked by a test, because there is no native test project.** What
  it buys today is a file that can be read without knowing which framework it is being compiled for,
  and a compiler that refuses the wrong dependency. Testability is what it makes possible: a tier-2
  translation unit can be compiled against a mock `IVisualTreeService` and a mock `IRoseOverlay`,
  where a tier-3 one would drag in a whole cppwinrt projection to exercise a path-parsing function.
  Do not cite tests as the reason for the boundary until something actually tests it.
  <br>
  `TapTree` is the one piece that needs no mock at all: it holds three containers, answers questions
  about them, and reaches nothing -- not the framework, not the site, not the overlay. A test for
  `Resolve` builds a node list, asks, and checks the answer. That it can be tested that easily is why
  addressing lives there rather than on the object holding the framework's interfaces.
- **Both providers compile every tap change, and one of them is not a sample of the other.** The
  point of the alias split is that one source serves two frameworks, so a change verified under one
  has verified half of it. The tap is not built by `dotnet build` -- CI notes that `dotnet build` is
  green with both providers broken -- so the builds are explicit:

  ```
  ./src/RoseMcp.Xaml.Uwp.Tap/build.ps1   -Platform x64 -Configuration Debug
  ./src/RoseMcp.Xaml.WinUi.Tap/build.ps1 -Platform x64 -Configuration Debug
  ```

  Exit 3 is a missing toolset rather than a break, which is what lets a machine without the C++
  workload skip rather than fail. CI compiles both providers for x86, x64 and arm64 on `main`, and
  x64 only on a pull request.
