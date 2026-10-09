# Documentation is read as prose, and as much of it as the question needs

**Decision.** No read returns documentation XML. A summary is rendered once, by `DocumentationText`,
with every `<see cref/>`, `<paramref/>` and `<typeparamref/>` written as the name it points at and
the markup gone. `rose_outline`'s `includeDocumentation` gives the first sentence of each summary.
`rose_symbol_info` gives the whole summary as `summary`, every paragraph flattened to one line, cut at
a sentence past 1,000 characters with a notice saying how long it really is. `remarks`, `returns`,
`param` and `exception` are not given by either tool.

**The failure it prevents.** `rose_symbol_info` returned `GetDocumentationCommentXml()` verbatim: the
`<member name="T:...">` wrapper, each reference as a fully qualified documentation ID, list markup
and the author's indentation. On a well-documented library type that was 9 KB whose answer was about
200 characters, and a library type is where the tool is reached for first. The outline promised "the
first line" and returned the whole summary, so one member could cost twice what a grep of the whole
file did, and the caller who had been told the switch was cheap was the one who paid.

**Why one sentence in an outline and the whole summary in symbol info.** They answer different
questions. An outline is for choosing which members to look at, across every member of a type, and a
paragraph per member turns a list into a document. Symbol info is about one symbol the caller has
already chosen, and a summary is what its author wrote to say what it is: cutting it to one sentence
there would send the caller on to read the file, which is the read both tools exist to replace.

**Why a ceiling on symbol info's summary, and a notice rather than an argument.** A summary is meant
to be a few sentences, and the ones that run past a thousand characters are remarks written into the
summary, nearly always in a referenced assembly. The ceiling keeps one such type from costing what
the raw XML did. It is said in a notice because there is nothing a caller can do to get more: no
argument raises it, and an argument whose only use is to read one long summary in full is a cost
every caller pays in the tool listing for a case that has not come up.

**Where a sentence ends.** At a full stop, question or exclamation mark followed by a space and
anything but a lower-case letter, or at the edge of a paragraph or list entry, whichever comes first.
Not inside `<c>`, `<code>` or a reference's text, since a full stop there is part of a name; not after
an abbreviation such as `e.g.`; and never at the point in a version number, which has no space after
it. Decided while rendering rather than over the finished string, because once rendered a full stop
inside `<c>Path.GetFileName</c>` and one ending a sentence look the same. A first sentence longer than
300 characters is cut at a word and marked with an ellipsis, so that a summary written as one run-on
paragraph is still bounded.

**Why the rest of the comment is left out.** The finding behind this asked for parsed `remarks`,
`returns` and `params` as well. For a symbol declared in source they are already one switch away:
`includeSource` returns the declaration with its whole documentation comment, which is the text the
author wrote. For a referenced assembly's symbol they are the one thing no tool here gives, and they
would cost an argument on a surface that is held to a single ceiling with almost nothing left under
it. The question that brought a caller to a library type -- what is it, and what can I call on it --
is answered by the summary and the member listing with signatures. Revisit when the surface is split
into per-server budgets (#339) or when a caller is found needing a library method's parameter prose,
whichever comes first.
