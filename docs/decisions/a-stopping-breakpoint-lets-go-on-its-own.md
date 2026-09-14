# A stopping breakpoint lets go of the target on its own

**Decision.** A breakpoint that stops the target holds it until somebody continues or a safety
timeout expires, whichever comes first. The timeout is set per breakpoint and defaults to 30 seconds.
A person in the inspector can suspend it with a hold, and a hold is capped at ten minutes.

**Why.** A stopped target is a frozen application: its window stops painting and its UI thread
answers nothing. An agent that sets a breakpoint and then runs out of turns, loses its connection or
simply moves on would otherwise leave somebody's app wedged indefinitely, with nothing on screen to
say why. The timeout makes the worst case a pause of seconds.

**Why 30 seconds.** Long enough for an agent to read the stop in its next turn and decide what to do,
short enough that a stop nobody reads is an annoyance rather than a hang. It is a default rather than
a constant, so a caller that knows it needs longer can say so when it sets the breakpoint.

**Why the hold exists, why it is capped, and why agents do not get it.** A person reading a stack needs
the target to stay still for as long as they are reading, which can be minutes. An agent has no way to
notice it has walked away, so the hold is on the operator API only, and even there a hold that
outlives its reader runs out. See [the debugging UI](debugging-ui.md).
