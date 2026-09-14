# A live edit's value falls back to the property's declared type

**Decision.** When `rose_xaml_apply` sets a property, the host sends a type hint inferred from the
property's name and the value's shape. If the provider cannot set the property with that type, it tries
the property's own declared value type, read from the live property chain. A failure names the type
that was tried.

**Why a hint first.** It carries intent the runtime does not have. A colour string is meant as a
`SolidColorBrush` even where the property currently holds some other brush, so trying the declared type
first -- `Brush` -- would break brush edits.

**Why the declared type as the fallback.** A hint inferred from a string's shape is a guess.
`CornerRadius="0"` looks like a number, goes out as a `Double`, is created without complaint, and then
fails at `SetProperty` with a bare `E_FAIL` naming neither the property nor the type. A table of property
names can fix each such case as somebody finds it and will never be complete. The declared type is a
fact, so with it as the fallback the table is an optimisation and an unlisted property still works.

**Why the type is in the failure.** `E_FAIL` from one layer below where the mistake was made is not an
error message. `SetProperty(Windows.Foundation.Double) failed` is.
