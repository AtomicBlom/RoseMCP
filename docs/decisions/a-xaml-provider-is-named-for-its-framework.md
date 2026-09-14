# A XAML provider is named for the framework it binds to

**Decision.** The native provider is split by XAML framework. `RoseMcp.Xaml.Uwp.Tap` binds to
`Windows.UI.Xaml` and `RoseMcp.Xaml.WinUi.Tap` to `Microsoft.UI.Xaml`, and the tap, the overlay and
the pipe they share live in `RoseMcp.Xaml.Tap` as headers.

**Why the framework and not the app model.** Classic UWP and UWP on modern .NET both run
`Windows.UI.Xaml`, so a name like `UwpClassic` would be wrong for half of what that provider serves.
WinUI 3 is a different DLL to initialise, a different diagnostics endpoint and a different set of
projections, which earns it a binding of its own rather than a flag on the other.

**Why it matters past the name.** Which provider serves a target is decided by the framework the
running process has loaded, and an install carries both for every architecture it supports. A name
that says the framework is a name that says which one to load. See
[hosts-and-deploy](../invariants/hosts-and-deploy.md).
