# The panel's Ask is collected by the host and opens the Inspector

**Decision.** The in-app panel's Ask button writes a latch in the provider (`RoseTapAsk` in
`tap_surface.h`) and nothing else. The live-app host asks the provider for it once a second -- `ask
<seen>` on the pipe -- while a provider is resident and the target is not stopped, and keeps the latest
press it collected; its self-report (`LiveAppInfo.XamlAsk`) carries it. The broker reads that report on
the poll it already makes of every session, and on a press it has not opened a window for, made in the
last thirty seconds, it asks the host's `IInspectorPresenter` to open the Inspector on that session --
the same launch the tray's Inspect button makes, which brings forward a window already open on the
process. The session summary carries the press too (`LiveAppSessionSummary.XamlAsk`, with its age), and
the Inspector, reading its session as it always does, opens its XAML view on the element the press was
about and its note composer with the subject filled in: the pick, a pinned measurement or the lens's
colour. The person writes the note and sends it; the press sends nothing to an agent.

**Why a latch the host asks for, and not a message from the app.** The pipe carries requests from the
host and replies to them, and that is what makes it safe to have in somebody else's application: a reply
is matched to its request by id, every request is bounded, and nothing on the host side has to listen for
the app. A press pushed from the app would be the one frame nobody asked for. The latch is read on the
provider's reader thread and holds no XAML, so asking for it costs the app's UI thread nothing; the only
UI-thread work is taking the chip's accent off once the press has been collected, which is why the host
does not ask while the target is stopped -- the reader would wait on a UI thread the stop is holding.

**Why the host asks, and not the RoseMCP app.** The point of the button is a person who is looking at
the app and not at any window of ours, so nothing in the RoseMCP app can be relied on to be polling:
there may be no window, and no app. The host is the one party that is always there while a session is,
and the broker already polls it every second, so the press reaches the broker without a new hop or a new
timer in the broker.

**Why not the named event #226 proposed.** An event set by the toolbar and waited on by the host would
avoid the once-a-second exchange, but it needs a second channel into the app with its own lifetime --
created where, granted to which package, cleaned up when -- for a cost the latch does not have: an
exchange answered on the reader thread is a fraction of a millisecond, and nothing is asked of a session
with no provider resident, which is every session nobody has pointed a XAML tool at.

**Why the note opens in the Inspector.** The panel takes no text and stays icon-only, so what it can hand
over is a subject, not a question. The Inspector already has the composer, the recipients and the
delivery of notes, and a press that opened a note nobody could edit would send words the person did not
write.

**What it does not do.** The press is not written anywhere an agent reads: only a note the person sends
reaches an agent. A press made before the broker was watching -- a broker that restarted, a press more
than thirty seconds old -- opens nothing, and a window reading a press more than a minute old does not
open a note about it, since the press could not have been what opened the window.
