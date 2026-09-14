# The in-app overlay

Read before changing `src/RoseMcp.Xaml.Tap/tap_overlay.h` or `tap_measure.h`.

- **A diagnostics UI layer is asked for by XamlRoot, and on WinUI 3 the one-argument call is the wrong
  one.** `IXamlDiagnostics::GetUiLayer` takes no argument, and its own documentation says why that is a
  problem: `IXamlDiagnostics2` exists to add "XamlRoot-based APIs to replace IXamlDiagnostics APIs that
  assume there is only one window". A WinUI 3 desktop app can have several XamlRoots, so a layer asked
  for without naming one is not necessarily the layer for the root on screen -- and the one it returns
  lays out at the right size, reports `Visible`, opacity 1, holds its child, and is never painted.
  Every property that can be inspected says yes while the screen says no, which is why no amount of
  poking at the layer could find it: the answer was `GetUiLayerForXamlRoot`, on an interface nothing
  here had heard of. The handle it wants is the app's XamlRoot, resolved from an element the tree walk
  already enumerated -- and it must be *an element*, because the first node enumerated is the
  `DesktopWindowXamlSource` hosting the tree, which is not a `UIElement` and has no XamlRoot to give.
  <br>
  The toolbar was invisible on WinUI 3 from the day it shipped, and two independent blind spots kept it
  that way: the WinUI tests assert the tree and the properties but never that the adorner renders, and
  no WinUI provider was ever deployed, so nobody was in a position to look. Do not reach for a `Popup`
  or for the app's own content panel instead. Both were tried: a popup opened from the tap fail-fasts,
  which leaves no exception for a local catch *or* for an attached debugger, and hosting in the app's
  content puts an element in somebody else's tree and drags the toolbar into any transform applied to
  it. Both were treating the symptom.
  <br>
  The same wrong root was behind more than the toolbar. `Extent()` and the content the transform scales
  both read through the layer's `XamlRoot`, and the pointer seam's "the XamlRoot has no ContentIsland"
  complaint disappeared the moment the right one was used -- so the proximity fade had been quietly
  broken by the same cause. And beware the shape of the check that hid it: comparing the layer's
  XamlRoot against the XamlRoot of content fetched *from that same layer's XamlRoot* is circular, and
  reported "shared" no matter what was true.
- **The magnifier replicates pixels by hand, because XAML will not.** WPF has
  `RenderOptions.BitmapScalingMode`; UWP and WinUI have no bitmap scaling mode at all -- the only
  `InterpolationMode` either exposes is `ColorInterpolationMode` on a gradient brush -- so a
  `ScaleTransform` on an `Image` always filters and would smear exactly what is being inspected. So a
  capture is taken with `RenderTargetBitmap`, read as BGRA8, and replicated block by block into a
  `WriteableBitmap` sized so one bitmap pixel lands on one *device* pixel; left to XAML's layout it
  would be scaled by the rasterization scale and resampled, reintroducing the filtering at the last
  step. The hex readout is the centre pixel of that same buffer, so the swatch and the lens cannot
  disagree about what is under the cursor.
  <br>
  Nothing in the capture path may touch XAML off the UI thread. `RenderTargetBitmap` is a
  `DependencyObject`: reading `PixelWidth` in a completion handler is a wrong-thread call, and merely
  *capturing* the bitmap in that handler is worse, because the lambda is destroyed on the pool thread
  and takes the last reference with it. That crashed the app seconds after the mode was switched on,
  with the last line in the log being the one that says the mode is on. The handlers carry nothing but
  `this` and a bool; the bitmap and the pixel operation are members so no lambda owns them.
  <br>
  The transform mode's origin has to follow the pointer, and must never resolve to a corner. Set once
  when the mode was entered, it anchored wherever the pointer was during the click that turned the mode
  on -- which is on the toolbar -- and when the pointer is outside the window it reads (-1, -1) and
  clamps to (0, 0), so the app scaled about its top-left and at 4x nothing was on screen at all. An
  unknown pointer leaves the origin alone. The marks scale with it, on a canvas of their own: they are
  drawn at coordinates read out of the app, so an app that has been scaled leaves them behind, and they
  cannot each take the transform individually because each is positioned by `Canvas.Left` and a render
  transform scales about its own element rather than the canvas origin.
- **A measurement is placed by the ends of its line, and the ends are the part that carries meaning.**
  The rulers mode draws the gaps from the picked element to whatever the pointer is over, and where the
  number goes is most of whether it can be read. It sits centred on its own dimension line, with the
  line running behind the opaque badge, whenever the line is long enough to hold it with clearance
  left at each end. Where it is not -- a gap of four pixels has a line four pixels long and a number
  three times that wide -- it goes outside the line's end instead, on the side away from the anchor's
  centre, so the left inset's number sits left of everything it measures and the right inset's sits
  right. Centring it there anyway would cover both ends, and the ends are the kinks where the extension
  lines turn off to the two edges being measured: covering those hides which edges the number is
  about, which is the one thing a number cannot say for itself.
  <br>
  Numbers that still collide are pushed perpendicular to their own line and given a tie line back to
  its midpoint. Perpendicular and not along it, because sliding a number along its line slides it off
  the thing it measures, and two numbers on one axis would then appear in the order they were drawn
  rather than the order their edges are in. The tie is what a moved number needs and a number sitting
  on its line does not: once it is off the line there is nothing else saying which line it belongs to.
  The two element captions are seeded into the occupied set before any number is placed, because both
  sit at the top-left corner of a rectangle -- exactly where the top and left insets are measured. And
  a label is *measured* rather than read off `ActualWidth`, which is a fact about the last layout pass
  and is zero for a label being shown for the first time, so the first placement of a session would be
  decided by a rectangle nothing can overlap.
- **Both pointer modes share one capture layer, and the overlay's mode is a name rather than a flag.**
  Select and rulers each want the same full-bleed layer over the app, and two of those would be two
  things claiming every click, so there is one and the handlers branch on the mode. Switching between
  them keeps the layer already up: arming is what a caller waits on, and what it waits for is a layer
  XAML has arranged. The mode is reported by name because rulers captures the pointer exactly as
  select does -- a host that knew only about select would report an app nobody can click as idle, and
  the live-app fixture's hand-back check is built on precisely that signal. Clearing the pick stays a
  separate act from leaving the mode, or "keep this one and stop capturing my clicks" becomes
  unreachable.
- **No padding is not padding of zero, and XAML declares `Padding` on no common base.** `Control`,
  `Border`, `TextBlock`, `RichTextBlock`, `ContentPresenter`, `StackPanel`, `Grid` and `RelativePanel`
  each declare their own, so the box model asks those projections in turn, and a type with none is
  reported as having none. A band of zero width draws "this element has no padding" and "this element
  has a padding of nothing" identically, which is why the numbers live in the toolbar's own readout
  rather than on the bands: it is the only place that can say which sides are zero, and that a type
  has no padding at all. Those numbers spell all four sides even though the value came from a
  shorthand, since somebody reading that row is reading it because they cannot account for a few
  pixels, and that is not the moment to make them guess whether one number means one side or four.
- **The overlay asks its `XamlRoot`, not its window, and the pointer hook is the only seam left.**
  `Window.Current` does not exist in WinUI 3, and nine sites wanted three things of it: the extent,
  the root content, and a size-changed event. `XamlRoot` answers all three *and* exists on UWP since
  1903, so it is one implementation rather than a per-provider host surface -- worth checking rather
  than assuming, because the alternative was every line of it written twice. Every `Bounds()` use read
  only `Width` and `Height`, so `XamlRoot.Size` is a drop-in for a `Rect`. `XamlRoot.Changed` also
  fires on a scale change, which the window event does not: moving the app to a monitor at a different
  DPI resizes the XAML content without resizing the window, and the old handler slept through it.
  <br>
  What is left is `RoseTapWatchPointer`, which the provider defines before including `tap_overlay.h`,
  next to the aliases and the CLSID. It installs a **passive** observer of pointer movement for the
  proximity fade, and two properties are required of it: it must consume nothing, and it must see
  moves the app has already marked handled. UWP's `CoreWindow` has both. WinUI 3 has no `CoreWindow`,
  and `InputPointerSource` was *measured* against exactly those two rather than assumed equivalent --
  41 moves over plain content then 33 over an element whose handler sets `Handled`, and the island's
  source counted all 74 while the app's own root handler counted the 41 and none of the 33. Losing it
  costs the fade and nothing else: select mode picks through the full-bleed capture layer and never
  came through here, so a provider that cannot supply it logs and carries on.
  <br>
  Which island to ask is its own trap, and it is not the obvious one. `XamlRoot.ContentIsland` is
  null on WinUI 3 for the root the overlay anchors on: the framework fills it in only for a
  XamlIsland-based content root (`XamlRoot_Partial.cpp` asks `GetXamlIslandRootNoRef`), and the
  diagnostics UI layer is not one. So the fallback is `ContentIsland.FindAllForCurrentThread`, which
  is valid because this always runs on the UI thread, and the thread owns exactly one island -- so
  taking the first is not a guess. Both halves were measured: 40 synthetic moves swept across the
  window arrived as 40. It matters because the failure was silent and cheap-looking -- the marks stop
  fading and nothing else changes -- which is how it would have outlived several releases, and it is
  why each of the four steps now says which one gave up rather than the caller reporting only that
  the fade is off.
  <br>
  One overlay, in the root the diagnostics site handed us. A WinUI 3 app can have several windows and
  the snapshot enumerates all of them, so `SharesRoot` is asked before anything is drawn -- an element
  elsewhere is *said* to be elsewhere. It used to be told it had "no laid-out bounds", which is a
  confident wrong answer about an element laid out perfectly well, and `TransformToVisual` across two
  roots does not fail in a way that says otherwise. Drawing anyway would put the mark at the right
  coordinates in the wrong window, which is the failure #45 and #51 are already about.
