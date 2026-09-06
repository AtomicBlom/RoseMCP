# The worker does not reference Microsoft.CodeAnalysis.CSharp.Features

**Decision.** Rose does not take a dependency on `Microsoft.CodeAnalysis.CSharp.Features`, and so
offers no `rose_list_refactorings` / `rose_apply_refactoring` pair over Roslyn's own
`CodeRefactoringProvider` set.

**What it would buy.** Extract method, extract local, extract interface, inline method, introduce
parameter, generate constructor, generate `Equals` and `GetHashCode`, implement interface, and
convert-to-file-scoped-namespace, all through one pair of tools at a span. `CodeFixCatalog`
discovers `CodeFixProvider` types by reflection over analyzer assemblies and so already serves
analyzer fixers, including the SDK's `CodeStyle.Fixes`; the refactoring providers live in the IDE
layer and are absent.

**Why not.**

1. **Most of it is compositional here, and the composed version is verified.** Extract method is
   `rose_add_member` plus `rose_replace_body`, both of which parse before the file is opened and
   compile afterwards to report what the edit broke. Introduce parameter is
   `rose_change_signature` with an `arguments` mapping, which also updates every override,
   implementation and call site. What the IDE offers at a span, this offers by name, and a name
   survives an edit that a span does not.

2. **It version-couples the worker to Roslyn across a much larger surface.** The public analysis
   and workspace APIs are stable; the Features layer is where the IDE's own churn lands, and the
   worker already pins MSBuild resolution and analyzer loading tightly enough.

3. **It brings an add-import fix whose semantics contradict this server's.** `rose_resolve_name`
   exists because plenty of names live in two namespaces at once, and the wrong import is the
   worst kind of wrong: it compiles, and binds to the wrong type. So the search reports one
   namespace or a list, and never the first of several. The IDE's fix picks. Registering it beside
   a tool built to refuse would offer an agent two answers to one question, one of which is a
   guess, with nothing in either to say which it got.

**What changes the answer.** Evidence that agents want a span-addressed refactoring often enough
to pay for it -- which means dogfooding records showing `add_member` plus `replace_body` losing to
a text edit on an extract, not a judgement that the catalogue looks thin.
