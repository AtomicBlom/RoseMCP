# The in-app toolbar is a resident, click-through overlay

**Decision.** The first XAML request in a session puts a small toolbar -- the Rose panel -- on the app's
diagnostics UI layer, and it stays for the life of the app. The app underneath stays fully usable.
Select mode, which a person arms from the toolbar or the inspector, adds a full-window capture layer so
the next click picks an element instead of reaching the app. An agent reads the pick afterwards, and
can mark an element itself by handle, name or address.

**Why an overlay in the app rather than a mouse hook in the host.** It is what Visual Studio does, the
click that picks an element does not also reach the app, and the diagnostics UI layer exists for
adorners, so the app's own tree is never touched.

**Why resident, and why the person drives it.** The natural order is to point at something and then
talk about it. A select mode only an agent can arm forces the opposite: ask the agent, wait for it,
then click. So the toolbar is always there and the person arms it. The mode is read back from the app
rather than remembered by the host, because the person can arm or cancel it without the host being in
the conversation at all.

**Why no keyboard chord.** Apps implement their own modifier-clicks, and an overlay silently taking one
would be a collision nobody could diagnose.

**How click-through works with no hooks.** XAML's own hit testing draws the line. A panel whose
`Background` is null takes no part in hit testing, so the overlay's root lets every click through to
the app. The toolbar has a background, so it takes input. Select mode's capture layer has a transparent
background -- transparent, not null -- which does hit-test, and a faint tint, because a window that
swallows every click while looking unchanged reads as a hung app.

**Two rules that come from using it.** Everything on the layer is sized explicitly from the app's
`XamlRoot`: a stretched capture layer can arrange at zero size while the toolbar, which has a size of
its own, still draws and works, leaving select mode armed and deaf with nothing in the log. And the mark
on the toolbar is drawn as geometry from the same curve as the app icon, so it is exact at any DPI and
cannot drift from the brand.

The rules that keep the overlay correct are in [overlay](../invariants/overlay.md), and what it looks
like in use is on [the wiki](https://github.com/AtomicBlom/RoseMCP/wiki/The-Rose-panel).
