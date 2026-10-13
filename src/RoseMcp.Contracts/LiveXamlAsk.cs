namespace RoseMcp.Contracts;

/// <summary>
/// The latest press of the in-app panel's Ask button, as the live-app host collected it: what the person was
/// pointing at -- a pinned measurement, the pixel lens's colour, or the picked element -- in words and by handle.
/// <para>
/// A press carries no words of the person's own, because the panel takes no text: it opens the Inspector with a
/// note about this already filled in, and the person writes and sends the note there. So nothing the app does can
/// reach an agent through it.
/// </para>
/// </summary>
public sealed record LiveXamlAsk
{
	/// <summary>Counts the presses in this session from one; a window tells a new press from one it has handled by it.</summary>
	public required long Sequence { get; init; }

	/// <summary><c>measurement</c>, <c>colour</c> or <c>element</c>.</summary>
	public required string Kind { get; init; }

	/// <summary>What was on screen, in words, as the panel said it.</summary>
	public required string Readable { get; init; }

	/// <summary>The picked element, or a measurement's anchor; null for a colour.</summary>
	public ulong? Element { get; init; }

	/// <summary>The element a measurement was taken to; null for every other kind.</summary>
	public ulong? OtherElement { get; init; }

	/// <summary>The lens's colour as <c>#RRGGBB</c>; null for every other kind.</summary>
	public string? Colour { get; init; }

	/// <summary>
	/// When the press was made: when the host collected it, less the age the provider gave it. What decides whether
	/// anybody is still waiting on it.
	/// </summary>
	public required DateTime PressedUtc { get; init; }

	/// <summary>
	/// The press as a live-app host reads it off the provider, dated by the age the provider gave it: the panel outlives
	/// every session, so a press can be collected long after it was made.
	/// </summary>
	public static LiveXamlAsk From(XamlAskRow row, DateTime collectedUtc)
	{
		ArgumentNullException.ThrowIfNull(row);

		return new LiveXamlAsk
		{
			Sequence = row.Sequence,
			Kind = row.Kind,
			Readable = row.Readable,
			Element = row.Element,
			OtherElement = row.OtherElement,
			Colour = row.Colour,
			PressedUtc = collectedUtc - row.Age,
		};
	}

	/// <summary>
	/// Whether <paramref name="other"/> is this same press. By its number and when it was made together: a provider
	/// that reconnects numbers from wherever it is, and a number alone could be another press in a later connection.
	/// </summary>
	public bool IsSamePress(LiveXamlAsk? other) =>
		other is not null && other.Sequence == Sequence && other.PressedUtc == PressedUtc;
}
