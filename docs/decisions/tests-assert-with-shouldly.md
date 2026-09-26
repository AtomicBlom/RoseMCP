# Tests assert with Shouldly

**Decision.** The test projects assert with Shouldly, imported project-wide. The runner is TUnit;
its own assertions and xunit's are not referenced.

**Why.** A Shouldly failure names the expression that failed -- `result.Applied should be True but
was False` -- where xunit's says only that a value was false, and a suite this size is read mostly
through its failures. It also declares no `Assert` class, so nothing clashes with TUnit's: xunit's
had to be reached through a global alias for that reason alone. TUnit's own assertions are awaited
fluent chains, which is a rewrite of every line for a worse read at the site.

**Where Shouldly is looser, and what keeps the old strength.**

- String `ShouldContain`, `ShouldStartWith` and `ShouldEndWith` ignore case unless told otherwise.
  Every one is written with `Case.Sensitive`, or `Case.Insensitive` where the comparison was
  `OrdinalIgnoreCase`, so an assertion accepts no text the comparison it states would reject.
- `Should.Throw<T>` accepts a derived exception, where xunit's `Throws<T>` required `T` itself. A
  synchronous throw is followed by `.ShouldBeOfType<T>()`; an asynchronous one by `.OfExactType()`
  from `RoseMcp.TestSupport`.
- `ShouldContain(predicate)` takes an expression tree, which cannot hold `?.` or an `is` pattern.
  Those few are written `xs.Any(p).ShouldBeTrue()`, which loses the item listing in the failure but
  not the check.

**What it costs.** Shouldly's `actual` is not nullable, so a `string?` subject needs a `!` that xunit
did not ask for. The assertion still fails on null; the `!` only tells the compiler so.
