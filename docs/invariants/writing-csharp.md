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
  every line, and it leaves multi-line verbatim and raw literals alone -- in every branch of an
  `#if`, since an inactive one is disabled text no node walk finds -- because a newline in one is
  content and a raw literal's indentation decides how much is stripped from it. Both passes take a
  span when the caller wrote one member rather than a file: a repository whose endings are already
  inconsistent would otherwise have every line rewritten by a one-member change, which buries the
  edit in a diff nobody can review.
- **A file's layout is decided once per write, from the file as it was, and the formatter is told
  it.** When each pass worked a file's indentation and ending out for itself, the answers disagreed,
  on lines nothing asked to change:
  - Roslyn's formatter, told nothing, sets the whitespace in front of the token after what it formats
    with its own four spaces. So an edit in a tab-indented file with no .editorconfig re-indented the
    member after it.
  - A new file took the ending of the code the caller sent, which is LF whatever the repository uses.

  `Whitespace.RulesForAsync` reads what the repository declares: an .editorconfig covering the file,
  from Roslyn or from disk where the project was never given one, then .gitattributes for the
  ending. Otherwise it reads what the file already does or, for a new file, what the files nearest
  it do. It never reads the payload. Every pass takes that one value: `FormattingOptionsAsync` fills
  in the formatter's options wherever Roslyn wasn't told, and the text pass uses the same rules. See
  [the decision](../decisions/a-files-layout-comes-from-what-declares-it-then-from-the-file.md).
- **A change a diff cannot show is said in words.** A unified diff compares the content of lines,
  and a terminator is not content -- so rewriting a file's endings produces no hunk at all. That is
  the change `rose_format` is called for most often, in exactly the repositories where it matters:
  where IDE0055 is an error, an LF in a CRLF file is a failed build, and fixing it is the whole
  reason the call was made. Reporting five changed files beside an empty diff reads precisely like a
  call that did nothing. So `SolutionWriter` counts the lines that moved and every writing tool
  passes the sentence on, rather than the alternatives: a whole-file hunk nobody can read, or
  inventing a hunk header that is not a patch.
- **A write names every line it changed that nothing it was asked to do reaches.** Layout the
  formatter has no rule about is layout nothing checks. A body reflowed by an insertion, a value
  pulled up onto its declaration's line, a comment dropped from between two matched statements: each
  compiles, passes `dotnet format` and reports success. A caller who can find any of it only by
  reading the file back has no reason left to use a tool rather than a text edit. So
  `EditPipeline.WriteAsync` takes an `Asked`, the spans of each file as it was that the request
  reaches. `Overreach` diffs the lines and names every one that changed outside those spans, first
  among what the result says. The spans are declared by the tool that makes the edit, where the
  rewrite is worked out, rather than inferred from what changed, because a span drawn to fit the
  change would cover the damage it exists to find. Each is as narrow as the request:
  - A whole body asks for the body, and for the end of the signature only when an arrow trades
    places with a block.
  - An anchor asks for each token it matched, so a comment between two of them is not the caller's.
  - An insertion asks for the place it goes.

  A diff pairs identical lines arbitrarily, so where it puts a run isn't always where the edit put
  it. A run that went in or came out with nothing in its place is asked for if any place it slides
  to is, since a documented member added in front of another begins with the same `/// <summary>`.
  Blank lines beside what was asked go with it for the same reason. It is a sentence rather than a
  refusal, because a line can change harmlessly, such as
  trailing whitespace trimmed where the file asks for it, and only the caller holding the diff can
  tell that from a reflow. It says nothing about what happens inside the spans: a replacement
  written at the wrong depth is still the replacement the caller asked for.
- **`rose_format` says what it checked, never that a file is formatted.** It applies Roslyn's
  formatter and the whitespace pass, which is what `dotnet format`'s whitespace check, IDE0055,
  applies, so a clean result means that check will pass the file, not that a reviewer will. It
  runs none of `dotnet format`'s style or analyzer passes. Neither the formatter nor IDE0055 has a
  rule for where a line wraps or how deep a wrapped line sits: a list written two levels deep, a
  parameter list joined onto one long line and a continuation indented twice all pass both. So a
  run with nothing to change says that it met those rules and that wrapping is outside them, and
  the description says the same. Giving it the whitespace half of
  `dotnet format --verify-no-changes` would add nothing: that check is the same formatter, so it
  passes every one of those shapes, and the one it does catch, a run of whitespace between two tokens on a line,
  the formatter already rewrites here. Of the layout nothing has a rule for, one shape is told
  mechanically and reported by line: items of one list wrapped one to a line beginning at
  different depths, which no file does on purpose and which is what a splice leaves when it adds
  the destination's indentation to code that already had it. Depth on its own is not judged, since
  a list two levels deep throughout is a convention some repositories choose, and a list with more
  than one item on a line is passed over, since a table aligned by hand starts its rows wherever
  its columns line up. Nothing is rewritten, since which depth was meant is exactly what cannot be
  told. While anything is reported, the headline does not call the file clean.
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
- **A write made in rounds finds what it marked, not where it was.** A tool that rewrites one part of
  a file and then another cannot look the second up by its span from before the first. Qualifying a
  call above a declaration moves the declaration, and a lookup that finds nothing and carries on
  leaves a move's source in place: the member declared in both types, in code that compiles because
  the types differ, so neither verification nor the overreach sentence says a word. So
  `MoveMemberService` annotates the declaration and the type it goes into before anything is
  rewritten, finds both by annotation afterwards, and treats a mark it cannot find as an error rather
  than returning the solution unchanged.
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
  fix changes what the program says. Where code a caller supplied had its bare LFs rewritten, only
  the endings inside a literal's value are reported, each literal by its line in what the caller
  sent: every other ending is layout, and a sentence on every write is one nobody reads. A new
  file is asked as a whole, before its namespace and imports are put around it, so its literals are
  rewritten like a member's and named on the caller's lines rather than the file's. Literals
  are looked for in every branch of an `#if`, not only the ones the lexer took as active, because
  the rewrite reaches all of them and the build that defines the symbol compiles what it changed. See
  [the decision](../decisions/line-endings-in-code-a-caller-supplies.md).
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
- **A parameter list keeps the file's layout unless the caller wrote one.** A list written on one
  line says what the parameters are, not where they go, so each parameter that stays keeps its own
  line, indentation and comments, a new one takes the line of the parameter before it, and the
  commas are the file's -- the way a call site keeps its arguments. Rebuilding from the caller's text
  collapses a constructor wrapped one parameter to a line into a single line hundreds of characters
  long, and drops the comments that grouped its parameters without a word. A list the caller wrapped
  is a layout they chose and is used, but the comments above existing parameters stay, because a
  comment is not layout.
- **An argument's indentation belongs to the line it begins, not to the argument.** A call site
  keeps its own arguments, commas and wrapping. An argument that is new, or that a change moves to
  another position, is laid out by the token in front of it: at the call's continuation indentation
  after a token that ends a line, with no whitespace of its own after one that does not, and with
  any comment the caller wrote in front of it kept. Copying a neighbour's leading whitespace is
  right only where every argument begins a line. Where several share a continuation line it writes
  a run of tabs in the middle of that line, which compiles, verifies clean and is exactly the
  argument list the change asked for, so nothing but `dotnet format` would ever say so. A blank
  line above an argument that still begins a line stays, and stays empty. Comments go with the
  argument they are about, not with a position: one ending the line after a comma belongs to the
  argument before it, and one between a comma and the next argument on the same line belongs to
  that argument. A comma the list gains copies the last one's layout but not its comment, and an
  argument that ends up last keeps the comment its comma carried, with the line break a line
  comment needs. Copying the comma whole writes the comment twice; dropping it with the comma
  deletes it, and neither is reported. The line break in front of a closing parenthesis written on
  its own line is the parenthesis's, though Roslyn hangs it on the last argument: it goes to
  whichever argument ends up last, or an argument appended after it puts its comma at column zero.
  A directive in front of an argument keeps a line break before it wherever the argument lands,
  since a directive that does not begin its line is CS1040.
  Nothing the rewrite writes may follow a line comment on its line. Every comma or argument given
  a comment carried from elsewhere ends its line after it, so the next argument starts a new line;
  otherwise the argument lands inside the comment, and a parameter with a default compiles without
  the value the caller passed. A call whose removed argument or comma carries a directive is left
  to a person and reported: the directive goes with the token it sits in front of, and an `#if`
  taken out without its `#endif` is CS1028.
- **A change of accessibility moves the override chain, and nothing else in the modifier list.** An
  override that keeps the old accessibility is CS0507, so the base all the way up and every override
  all the way down change with the member named. Interfaces are not part of that group: an implicit
  implementation is only an implementation while it is public, so narrowing one is refused, not
  written. Leaving it to the compile would report CS0737 on a type the caller never touched. Only the
  accessibility keywords are rewritten, where they stand, because `static` and `override` belong to
  the member and the documentation comment belongs to whichever token comes first. See
  [the decision](../decisions/accessibility-is-part-of-a-signature-change.md).
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
  rather than none. So does a candidate of the wrong kind, which is why the search is narrowed by
  how the code uses the name before anything is counted: a name called on its own is answered by no
  type, one after a dot only by an extension, one in a type's place only by a type. And an import
  that still leaves its own name failing, bound with the import in place, is taken back out before
  the write, since "the only namespace anything of that name is in" was true and not the one this
  code needs. Most of the value is in never being asked -- the write tools run the search
  over the errors they introduced, off the compilation they had just built to find them, so the
  namespace arrives with the error rather than a call later. The IDE's own add-import fix is no
  route to any of it: that lives in `Microsoft.CodeAnalysis.CSharp.Features`, which is not
  referenced, and what *is* registered for CS0103 offers to generate the missing member -- the
  wrong fix, confidently, for a name that exists already.
- **A structural rewrite moves the caller's syntax; it never regenerates it.** What a
  `rose_replace_pattern` placeholder captured is written back as the node it was, comments beside
  it included, so a verbatim string, a cast or the spelling of a name arrives exactly as written.
  Only the layout at its edges is dropped, because that described where it used to sit. Where a
  capture becomes a receiver it is parenthesised unless it is a form that cannot need it, and a
  conditional access always is: `a?.B.ShouldBe(1)` compiles and skips the assertion whenever `a` is
  null. The formatter is given each replacement's own span, not its full span, since the indentation
  in front of it is the caller's.
- **A replacement that does not compile is never written.** Every replacement is compiled in
  memory first; one that brings an error it did not have before is put back, with the compiler's
  reason in the result, and never handed to a later rule -- that fall-through is how a string check
  lands on a collection rule with its arguments reversed. A preview runs the same rounds, so its
  list of skipped sites is the list an apply would skip. See
  [the decision](../decisions/code-is-rewritten-by-what-it-binds-to.md).
