# The visual tree is rooted and paged in the host

**Decision.** `rose_xaml_tree` takes `root` (a handle, an `x:Name` as `#name`, or an address), `offset`
and `limit`, and applies them in the live-app host over the provider's full snapshot. The result carries
the total that matched, so a caller knows whether more remain.

**Why page at all.** A real app's tree runs to thousands of elements, which is far more than an agent
should pull into its context to find one subtree.

**Why in the host rather than in the provider.** The provider has to enumerate the whole tree to answer
anything, so filtering inside the app would save nothing: the cost is the enumeration, not the transfer.
Doing it in the host keeps the code that runs inside somebody else's application as small as it can be.
