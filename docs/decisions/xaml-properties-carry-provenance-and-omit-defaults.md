# An element's properties come with what set them, and defaults only on request

**Decision.** `rose_xaml_properties` returns an element's effective property values, each with its
type and its provenance -- `Local`, `Style`, `Inherited`, `Animation`, `Default` and the rest of XAML's
base value sources -- and, where the app carries XAML source info, the file and line that set it.
Properties holding the framework's default are left out unless the caller passes `includeDefaults`.

**Why provenance.** The same value means different things depending on where it came from, and where
it came from is what somebody acts on: which file to edit, or whether the cause is a style nobody
reading the markup would think to open.

**Why defaults are left out.** An element has hundreds of properties and almost all of them are
defaults. Returning them buries the handful anybody set, and costs an agent context for nothing.

**Why source info is optional.** It needs the app built with XAML line info and running with XAML
diagnostics source info enabled. Without it the file and line are null and the values and provenance
still arrive, so a caller gets a smaller answer rather than none. For a UWP app launched from birth,
the host turns source info on in the activation environment it controls.

**A limit worth knowing.** Reading a `TextBlock`'s properties materialises its collection properties,
so a second read reports some as `Local` that the markup never set. The first read is the accurate one.
[Using the debug tools](../debug/using-the-debug-tools.md) says so where a caller will read it.
