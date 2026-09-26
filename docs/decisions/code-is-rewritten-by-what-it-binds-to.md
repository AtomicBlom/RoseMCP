# Code is rewritten by what it binds to, not by how it is spelled

**Decision.** `rose_replace_pattern` takes rules written as C# with placeholders -- `$x$` any
expression, `$x:Type$` one of that type or a subtype, `$x:id$` an identifier, `$T$` a type argument --
and rewrites every call or statement a rule matches. A rule matches by the symbols the code binds
to, compared on the operation tree, so a named or reordered argument, an alias, a `using static`
and either form of an extension call all match the same rule without a rule for each.

**How a pattern gets its symbols.** The pattern is compiled inside a scratch method added to the
target project's own compilation, so a name in it resolves exactly as it would in that project:
through the project's global aliases, its implicit usings and its references. Each placeholder is
a parameter of the method, typed by its constraint or `object` when it has none.

The pattern is not then bound to one overload, because an untyped placeholder makes that the wrong
question: `Assert.Equal(object, object)` resolves cleanly to `Equal<object>`, which is one overload
and the wrong one. The method *group* is taken instead (`GetMemberGroup`, which answers whether or
not resolution succeeded), narrowed to the members the pattern's shape fits: its argument count,
its named arguments, its type-argument count. A target matches when the method it calls is one of
those, compared by `OriginalDefinition` so a generic method matches for every type argument.

Two alternatives lost:

- **Declaring untyped placeholders `dynamic`.** A dynamic call has no target method to compare, only
  late-bound candidates, and a lambda cannot be the argument of one (CS1977) -- which is the shape
  of every predicate, `All` and `Throws` rule.
- **Comparing syntax and checking that names resolve alike.** Simpler to start, but every
  equivalence the operation tree gives for free -- argument order, `using static`, the reduced and
  static forms of an extension call -- would be a special case written by hand, and each missing
  one is a site that silently fails to match.

**What decides which overload a rule covers.** Its shape, never its placeholder types. A typed
placeholder filters what a rule captures; it does not pick an overload, because picking would make
`Single($xs:IEnumerable$)` bind to the non-generic `Single` and miss every `Single<T>` site. A rule
is narrowed by a type (`$s:string$`), by naming the parameter an argument belongs to
(`Assert.Contains($xs$, filter: $p$)` fits only the overload with a `filter`), or by order: at each
site the first rule that matches wins.

Order is not enough on its own, because two overloads can have the same shape with their roles
reversed. `Contains(collection, filter)` and `Contains(expected, collection)` both take two
arguments, so an unnamed `Contains($xs$, $p$)` placed first captures every item check with the
collection and the item swapped -- and the rewrite compiles. So a rule that an earlier rule
shadows is refused when the rules are read, and a preview names the sites more than one rule
matched.

**A replacement that would not compile is not written.** Every replacement is applied in memory,
the files that changed are compiled, and each error they did not have before is charged to a site:
the one whose replacement contains it; else the one that declared a local the erroring line uses,
because `var x = Assert.Single(...)` breaks on the line that reads `x`, not on its own; else every
site in the containing member. The charged sites are put back and the round repeats, a bounded
number of times, after which a file still gaining errors is left as it was. A preview runs the same
rounds, so what it lists as skipped is what an apply would skip.

A site that is put back does not fall through to a later rule. Falling through is exactly how a
string check refused by the string rule would land on the collection rule with its arguments
reversed.

Writing the replacement and letting the compile afterwards report it was the alternative: faster,
but it writes code that does not build. Leaving the whole file on any error was the other: one bad
site then blocks fifty good ones.

**A capture keeps its own text.** A capture is the caller's syntax moved into the template, with
its trivia, never regenerated. Where it becomes a receiver it is parenthesised unless it is one of
the forms that cannot need it -- a name, a member access, an invocation, an element access, a
literal, `x!` -- so `a?.B`, `await`, `as`, `??`, casts and negative literals always are.
`a?.B.ShouldBe(1)` compiles, and skips the assertion whenever `a` is null; an unneeded pair of
parentheses is the worst this rule can do.

Roslyn's simplifier does remove redundant parentheses, and lost anyway: it runs every reducer it
has across the annotated span, which includes the caller's own text inside the capture, and it
needs Workspaces, which the matcher is kept free of.

**The matcher does not reference Workspaces.** `RoseMcp.Patterns` references
`Microsoft.CodeAnalysis.CSharp` alone, so the parse, the binding, the match, the rewrite and the
rounds of compilation run anywhere a `Compilation` does -- including, later, inside an analyzer
that reports a rule's matches as diagnostics during a build. The worker supplies what needs a
`Solution`: import placement, formatting, and the edit pipeline. It targets `net10.0` like
`RoseMcp.XamlStubs`, and keeps to APIs an older Roslyn has, so moving it to `netstandard2.0` for an
IDE is a retarget rather than a rewrite.

**One tool, and it answers with a summary.** A preview (`apply=false`, as every write tool has) is
the structural search, so there is no second tool for it. At the size a mass rewrite runs to, a
diff is not something a caller can read, so the result counts: per rule, what it matched and which
overloads it covers; the skipped sites grouped by rule and compiler error; and the calls into the
same methods that no rule matched, grouped by overload. The last is how a caller knows a catalog is
complete -- matched plus unmatched adds up to every call, and an overload nobody wrote a rule for
shows up by name. The diff is included only while it is small enough to read.

**Rules are given with the call, not read from a file.** A file of rules is the format rules take
when they are reported as diagnostics, which is a design of its own; choosing it here would decide
that by accident.

**The matcher's unit tests compile against stubs.** They declare the shapes of the overloads they
match -- `Equal<T>`, the string `Contains` with and without a comparison, the collection and
predicate `Contains`, `Single`, `Throws<T>` -- in source, rather than referencing the assertion
libraries. A test that referenced the real packages would keep them in the repository's graph for
as long as it exists, including after the code under test has moved off them. The integration
fixture references the real packages, with their versions written into the fixture, so what the
tool does to real metadata is tested where it costs what it costs.

**What changes the answer.** A rule set that needs an overload picked by type rather than filtered
by it -- evidence, from a real catalog, that shape, parameter names and order cannot say which
overload is meant.
