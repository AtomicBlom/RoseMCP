namespace RoseMcp.Contracts;

/// <summary>
/// What an agent reading from a workspace is told about a reason a person has accepted.
/// <para>
/// The person's choice rather than a fixed rule, because the same acceptance means different things
/// for different reasons. Legacy test projects nobody works in are noise to every agent, and saying
/// nothing is right; a known gap an agent might still walk into is better said in the person's own
/// words, which can say when it matters; and some warnings are accepted only so they stop drawing the
/// person's eye, while an agent should still hear them as Rose words them.
/// </para>
/// </summary>
public enum AgentsRead
{
	/// <summary>Rose's own line, the opening sentence of the reason, as an agent would read it unaccepted.</summary>
	RoseLine,

	/// <summary>The note the person wrote, in place of Rose's line.</summary>
	Note,

	/// <summary>Nothing: a read does not mention the reason at all. Status still lists it, marked accepted.</summary>
	Nothing,
}
