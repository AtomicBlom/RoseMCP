using System.Globalization;

using RoseMcp.Tap.Protocol;

namespace RoseMcp.Contracts;

/// <summary>
/// The tap protocol versions a host accepts from one tap.
/// <para>
/// Exactly the host's own for a tap that ships with it, because then a mismatch can only be a stale
/// copy -- one left in a sandbox folder, or copied over by hand in a rebuild loop -- and that is exactly
/// when it should be refused by name rather than read as data. Wider for a tap dropped in from outside,
/// which is built and released apart from RoseMCP and would otherwise break at every protocol change
/// it had no part in. A version outside the range is still refused by name.
/// </para>
/// </summary>
/// <param name="Minimum">The oldest version accepted.</param>
/// <param name="Maximum">The newest version accepted, which is never past the host's own.</param>
public readonly record struct TapProtocolRange(int Minimum, int Maximum)
{
	/// <summary>
	/// The oldest protocol a dropped-in tap may speak: the first to declare capabilities, without which
	/// a host could only guess what a tap built apart from it serves.
	/// </summary>
	public const int OldestDroppedIn = 5;

	/// <summary>What a tap shipped with this host speaks: this host's own version and no other.</summary>
	public static TapProtocolRange InBox { get; } = new(TapGreeting.ProtocolVersion, TapGreeting.ProtocolVersion);

	/// <summary>What a tap dropped in from outside may speak: any version from the first with capabilities up to this host's own.</summary>
	public static TapProtocolRange DroppedIn { get; } = new(OldestDroppedIn, TapGreeting.ProtocolVersion);

	/// <summary>Whether <paramref name="version"/> is accepted.</summary>
	public bool Contains(int version) => version >= Minimum && version <= Maximum;

	/// <summary>The range as it reads in a sentence: <c>5</c>, or <c>5 to 7</c>.</summary>
	public override string ToString() => Minimum == Maximum
		? Minimum.ToString(CultureInfo.InvariantCulture)
		: $"{Minimum.ToString(CultureInfo.InvariantCulture)} to {Maximum.ToString(CultureInfo.InvariantCulture)}";
}
