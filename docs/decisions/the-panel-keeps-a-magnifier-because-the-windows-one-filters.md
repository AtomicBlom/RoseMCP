# The panel keeps a magnifier of its own, because the Windows one filters

**Decision.** The Rose panel carries its own magnifier, in two modes. The pixel lens magnifies a
capture of the app by replicating whole device pixels, and reads the colour of the one under the
pointer. Scaling the app puts a render transform on its live content. Neither is replaced by the
magnifier Windows ships.

**Why not the Windows magnifier.** It filters bilinearly, and it cannot be told not to. A filtered
image answers neither question the lens exists for. The colour under the pointer is a blend of its
neighbours, so it is never exactly the colour the app drew. And a one-pixel gap -- between a
`Border`'s stroke and its background where a corner radius bends, say -- is smeared into the stroke
at any magnification, so whether an edge is one pixel or two cannot be seen at all. A person who
reaches for the Windows magnifier to check a colour gets a wrong one and has no way to know.

So the lens takes a capture and shows the pixel grid the app actually produced, each pixel a block of
identical device pixels. XAML gives no help here either, which is why the replication is done by
hand; the rules that keep it exact are in [overlay](../invariants/overlay.md).

**Why the transform mode stays beside it.** A framebuffer magnifier resamples what was drawn, at any
filter setting. Scaling the live tree has the app draw again at the new size, so text and vectors stay
sharp: it is the mode for fine typography and geometry, as the lens is the mode for pixels and
colours. Neither substitutes for the other.

**What lost.** Deleting the feature as a duplicate of the Windows magnifier. A review recommended
exactly that, on exactly that reasoning, because nothing beside the code said why the platform's
version does not suffice. That is the general point: a feature that seems to duplicate something the
platform offers is the first thing a reader cuts, so the reason the platform's version falls short is
load-bearing and is written beside the feature -- in the file header, in this record, and in the
tooltip, which states the benefit a person gets rather than the mechanism that provides it.
