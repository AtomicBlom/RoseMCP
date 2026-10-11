# A note to an agent rides on its session's next answer

**Decision.** A person sends an agent session a note from the RoseMCP window -- `POST /operator/notes`, behind
the operator token -- naming the client by its row in the client table, a structured subject, and the person's
text. The broker holds it in `AgentNotes`, in memory, under the key the client table files that client under,
and the call filter `WaitingNotes` adds every note waiting for a session to the notices of the answer to that
session's next call, of any tool, marking each delivered with that call's activity entry, its tool and the time.
The agent answers with `rose_reply`, which reaches only the notes sent to the session calling it; the answer
shows under the note in the window, and the tray says so while no window is open. `GET /operator/notes` lists
every note with where it has got to and its answers, held open by a long poll like every other view.

**Why the notices, and not a field or a push.** Every result already has a `notices` array that agents read as
remarks about the answer, the ignored-argument notice adds one where a result has none, and a client reading the
text block reads the same words. A field of its own would be a second channel for one kind of remark, which a
model learns to read or not on the strength of how often it carries anything. Delivery on the next call works for
every client, headless sessions included, and needs nothing from the client. A push -- a Claude Code channel
message through the relay -- reaches a session that is not calling, but during the preview it needs a session
started with the development-channels flag or Rose installed as a plugin, and on a Team or Enterprise account an
administrator turning channels on; Claude Code does not acknowledge a channel message, so a pushed note would be
"pushed, not yet confirmed" until the session's next call or answer said it arrived. That is its own change, with
its own relay protocol and plugin packaging, and it was left out of this one. `NoteDelivery` is shaped for it: a
`Pushed` state slots between `Waiting` and `Delivered`, and a reader meeting a state it does not know treats the
note as waiting.

**Why the client's table key, and not its row id.** A row id is what a window has, so a note is addressed by
one. But a row goes two minutes after a relay lets go and is made again, with a new id, by the session's next
call, so a note held by row id would be stranded by a relay re-opening its watch. Held under the key -- the
session a relay names, else the transport's session -- the note reaches the session whatever row it has by then,
and a window is told the row the session has now. The key itself never leaves the broker: a relay's session name
scopes acknowledgements and notes and is carried by no answer, so a window names a note's client by row id and by
the hashed key the activity record uses.

**Why the unnamed client is refused.** Every client with neither a relay nor a transport session shares one
row, because nothing in its requests tells them apart. A note to that row would ride on whichever of them called
next, which may be a different agent from the one whose call the person was reading. So it is refused, saying
that the relay is how a session gets notes, rather than delivered to somebody.

**What delivers, and what does not.** Only an answer to a tool call. A listing is not a turn an agent reads
notices from, and a relay's own read of what its session has open is not one of the session's calls. A call that
is refused or fails reaches its client as an error the SDK writes once every filter has finished, with nowhere to
put a note; a cancelled call's answer is not one its client waits for. In each case the note waits for the next
answer, rather than counting as delivered to an agent that never saw it. The claim is made once the answer is in
hand, under the store's gate, so two calls answered together never both carry a note; an answer the notices
cannot be added to gives its notes back. "Delivered" therefore means handed to the transport in an answer: an
answer lost on the wire after that is not something the broker can see.

**What an agent can do.** Answer, and nothing else. A note reaches an agent as the person's words, so sending
one, changing one or reading another session's is the person's, on the operator API: an agent able to send one
could speak for the person to every other agent on the machine. `rose_reply` finds a note by its id among the
calling session's own and answers any other id as a note that does not exist, so a session cannot learn of
another's notes by guessing. An answer to a note not yet delivered marks it delivered, since an answer is proof
it arrived. A note keeps at most eight answers. The tool is not routed in the server's instructions: the note's
own notice names it and the id it takes, and a session has no use for it before a note arrives.

**What a session going does to a note.** Nothing silent. A note whose client the table no longer lists reads as
`ClientGone` -- still delivered if that session calls again -- and the Agents view keeps listing it after the
cards. Such a note is kept for a day from the later of its session going and anything last happening to it, so
one delivered or answered long before its session ended is still shown for a day after; the store holds 256 notes, sixteen
waiting per session, and past that makes room from delivered notes first and then from those whose session has
gone, never from one waiting for a session that is still connected, refusing the new note instead.

**Why in memory.** A note is about live state -- a call just made, a value at a stop, a reason the workspace has
now -- and a broker restart ends the debug sessions, the client rows and the relays' MCP sessions it was about.
Persisting notes would mean persisting the key they are held under, which is the one thing the broker keeps out
of every answer and every file. So a restart forgets every note, and `rose_reply` says so when asked for one.
