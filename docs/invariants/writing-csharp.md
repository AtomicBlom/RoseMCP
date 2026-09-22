# Writing C#

Read before changing anything that emits or rewrites source under `src/RoseMcp.Worker/`.

- **A change that reaches another solution says so.** Roslyn renames within one `Solution` and writes
  to disk, where every other solution over the same projects picks the new text up at its next read
  while still calling the old name from projects the renaming solution never had. That sibling is
  not stale, it is broken. Mutations report which solutions beside them compile the files they
  touched. Reported, not acted on: making a change complete across solutions means loading them all
  and merging the edits, and that is not always even well defined, since two solutions can build one
  project under configurations with no setting in common.
- **Whatever writes C# has to end formatted.** Roslyn's formatter honours `.editorconfig` but only
  rewrites the trivia it has reason to touch, so a file it reindents comes out with mixed line
  endings -- which IDE0055 then fails the build over. `Whitespace` is the second pass that fixes
  every line, and it leaves multi-line verbatim and raw literals alone, because a newline in one is
  content and a raw literal's indentation decides how much is stripped from it. Both passes take a
  span when the caller wrote one member rather than a file: a repository whose endings are already
  inconsistent would otherwise have every line rewritten by a one-member change, which buries the
  edit in a diff nobody can review.
- **A change a diff cannot show is said in words.** A unified diff compares the content of lines,
  and a terminator is not content -- so rewriting a file's endings produces no hunk at all. That is
  the change `rose_format` is called for most often, in exactly the repositories where it matters:
  where IDE0055 is an error, an LF in a CRLF file is a failed build, and fixing it is the whole
  reason the call was made. Reporting five changed files beside an empty diff reads precisely like a
  call that did nothing. So `SolutionWriter` counts the lines that moved and every writing tool
  passes the sentence on, rather than the alternatives: a whole-file hunk nobody can read, or
  inventing a hunk header that is not a patch.
- **A file goes back in the encoding it arrived in.** A byte order mark is part of the file, and the
  two calls that look like the obvious way to do this get it wrong in opposite directions.
  `File.WriteAllText` is UTF-8 *without* a mark whatever the file was, so a rewrite routed through it
  strips the mark off every file that had one; `Encoding.UTF8` is UTF-8 *with* a preamble, so handing
  it to `SourceText.From` as a stream's fallback gives every mark-less file an encoding that writes a
  mark it never had. Detection wins wherever a mark is actually there, so that fallback decides only
  the case it is named for -- which is what `SourceEncoding.Utf8WithoutMark` is, and why the read
  side needs it as much as the write side. Both failures are silent in the same way: three bytes no
  unified diff can show, on a file that compiles either way, surfacing as a review where every edited
  file changed at byte zero for a reason nobody can point at. Text carrying no encoding is text
  nothing read off disk -- a file being created -- and it gets the mark-less default rather than one
  it was never given. A split carries the source file's encoding into both halves for the same
  reason: the type moves, and the mark is not the type's to take with it or to leave behind.
- **Nothing writes code it has not parsed, and nothing is addressed by position.** The three write
  tools resolve the declaration and parse the code *before* the file is opened, so a refusal costs
  nothing and can never leave a file half-written -- which is most of the value, since it removes
  every failure a text splice produces by construction. Parsing happens inside a synthetic container
  of the same kind as the real one, because a member only means something in a container: a bare
  snippet parsed as a compilation unit turns `void M() { }` into a top-level local function, which
  parses cleanly and means something else. The shape is then checked as well as the syntax, because
  code that closes the container early and opens one of its own has no parse error at all and would
  smuggle a type into the file at top level. And a member is named, never pointed at: a line and
  column has to be found by reading the file first and is wrong the moment an earlier edit lands,
  which is how a text edit path produces an anchor found in the wrong place. Where a name matches
  more than one declaration it refuses and lists them, because writing correct code into the wrong
  overload is the only failure with no symptom at all.
- **Written code is indented for where it goes, because the formatter only does half of it.** Roslyn
  reindents statements and moves braces -- rules it has -- so a line wrapped by hand *inside a body*
  comes out right. A wrapped parameter list is layout it has no rule about, so it keeps whatever
  indentation arrived, and neither IDE0055 nor `dotnet format` says a word because neither of them
  has an opinion either. Code written for column zero therefore landed a level short of its
  neighbours, silently. `MemberSyntax` takes the code's own baseline indentation off every line and
  puts the destination's on: both halves, because a caller that has read the file and indented for
  the destination is as likely as one that wrote at column zero, and only removing the baseline
  first makes those the same request. Where the formatter *does* have a rule it still wins, since it
  runs afterwards. Replacing a body has the same trap from the other side: the signature is copied
  out of the file, and the span it is copied from begins *after* the indentation of its first line,
  so a wrapped parameter list read that way looks written at column zero and every continuation
  came out a level deep. Nothing downstream corrects it and nothing complains, so the signature
  drifted on a change that promised to touch only the body. That composition also puts two
  coordinate systems in one string, and one baseline cannot be read off both: the signature is
  indented for the file it came out of, while the body carries whatever the caller wrote it at.
  Taking the signature's indentation off the body strips a level from every line the caller wrapped
  by hand and nothing from the statements those lines belong to, so a wrapped call lands flat
  against its own statement -- again silently, since a continuation line is not a statement and the
  formatter has no rule that puts it back. The copied half is therefore named as copied and exempted
  from the pass, and what the caller wrote is what sets the baseline.
- **The line endings inside a string literal are content, and this was measured.** A raw literal
  written with CRLF and the same one written with LF are different strings -- the compiler says so,
  which is worth knowing because it is tempting to assume raw literals normalise and they do not. So
  nothing rewrites them, and `Shift` keeps each line's own ending rather than splitting on newlines
  and joining with one, which would have changed values inside the literals it was carefully not
  re-indenting. The consequence is reported rather than left silent: a multi-line literal written
  with endings the file does not use fails `dotnet format` while no build complains, and the obvious
  fix changes what the program says.
- **A signature change moves the whole declaration group, or it does not compile.** A virtual
  method whose override keeps the old parameters is a build error, and so is an interface member
  whose implementations keep theirs -- so `rose_change_signature` changes the member, its base
  declaration all the way up, the interface members it implements, and everything overriding or
  implementing those. Only the declaration the caller named gets the parameters they wrote; the rest
  are mapped by position and keep their own parameter names and attributes, because an override is
  free to call its parameters something else and replacing its list wholesale would rename them
  without saying so. Reordering existing parameters is refused rather than attempted: an argument's
  meaning at a call site is not always recoverable from its position. And the call sites that still
  compile are reported, because a forwarder that goes on passing the old default is the bug that
  hides -- "compiles" and "correct" part company exactly there.
- **An import goes where the file would have put it, and is refused when it is already in scope.**
  Writing a member is not the whole job: the code routinely needs an import the file has not got,
  and a tool that reports that and stops has handed the caller back to the text editing it was meant
  to replace, at the moment they had just been talked out of it. Placement is read from the file
  rather than from `.editorconfig` -- this repository sets
  `dotnet_separate_import_directive_groups = false` and every file separates its groups anyway,
  because the setting only stops the analyzer insisting -- and going in first means inheriting
  whatever sat above the old first line, since a licence header under an import changes what the
  file means to other tools. Whether it is needed is asked of the *compilation*, not of the import
  block: a global using, an implicit using from the SDK, and the namespace the file is in are all
  ways to be in scope without appearing there, and importing one of those again is IDE0005. Both
  halves of getting it wrong are build errors, which is the only reason it is worth this much code.
- **Which namespace a name needs is a search, and it refuses to pick.** Being told is the common
  case by a distance -- someone writing `Encoding.UTF8` knows it is `System.Text` -- and the rest
  is a question the compilation can already answer, since it holds every type in every referenced
  assembly. What it must not do is choose. Plenty of names live in two namespaces at once, and the
  wrong import is the worst shape of wrong here because it compiles and binds to the wrong type, so
  the answer is one namespace or a list, never the first of several. It is as careful about the
  things that look like an answer and are not: a nested type reachable only through its container,
  an arity that does not match the use site, a type in a project this one does not reference, and a
  namespace already in scope. Each of those turns adding the obvious using into a second error
  rather than none. Most of the value is in never being asked -- the write tools run the search
  over the errors they introduced, off the compilation they had just built to find them, so the
  namespace arrives with the error rather than a call later. The IDE's own add-import fix is no
  route to any of it: that lives in `Microsoft.CodeAnalysis.CSharp.Features`, which is not
  referenced, and what *is* registered for CS0103 offers to generate the missing member -- the
  wrong fix, confidently, for a name that exists already.
