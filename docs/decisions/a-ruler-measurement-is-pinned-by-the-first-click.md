# A ruler measurement is pinned by the first click, and a mode is left by its own button

**Decision.** In rulers mode with an anchor, a click on another element pins the measurement from the
anchor to it: the dimension lines stay on that element whatever the pointer does next, and the rulers
row says the numbers. A second click on the pinned element anchors there instead, a click on the anchor
lets the pin go, and leaving the mode or changing the pick lets it go too. The panel has no Idle button:
pressing the mode that is on -- Select or Rulers -- turns it off, and the host's `idle` request still
leaves whichever mode is on.

**Why the first click pins.** A measurement that follows the pointer is gone by the time the pointer
reaches the toolbar, so there was no way to send one to an agent, or even to read one out while looking
at something else. Pinning has to be one gesture over the thing measured, and the click is the only one
the capture layer already takes. Re-anchoring kept its gesture rather than losing it: it moved from one
click to two on the same element, which keeps the sweep from one element to the next a gesture rather
than a round of arming. A modifier key was the alternative and lost because the panel takes nothing the
app might want, and a chord is exactly that.

**Why the Idle button went.** A mode button that only ever armed left a separate button, a hand's width
away, as the one way out. Pressing the button that is lit to turn it off is where the hand already is,
and the toolbar loses a chip that did nothing else. Idle remains a state -- the host reports it by name,
and the live-app fixture's hand-back check reads it -- only not a button.
