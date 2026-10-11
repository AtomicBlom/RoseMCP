namespace RoseMcp.Server;

/// <summary>
/// Why a relay's watch on its broker ended, which decides whether the relay asks the broker before
/// replacing its connection.
/// <para>
/// A watch that closed is a reason to look: the broker may still be there, and asking it first keeps
/// a living broker's connection, and the debug sessions on it. A watch that reached another broker
/// instance is the answer already. Asking the old connection then proves nothing, since a broker
/// restarted on the same endpoint answers it -- with a refusal, which counts as an answer -- and the
/// relay would stay on a connection to a broker that no longer exists.
/// </para>
/// </summary>
public sealed record WatchEnd
{
	private WatchEnd(string? anotherInstance) => AnotherInstance = anotherInstance;

	/// <summary>The watch closed, or could not be held: the broker may have gone, and is asked first.</summary>
	public static WatchEnd Closed { get; } = new(anotherInstance: null);

	/// <summary>
	/// The broker instance the watch reached instead of the one the relay is connected to, or null
	/// where the watch simply ended.
	/// </summary>
	public string? AnotherInstance { get; }

	/// <summary>The watch reached <paramref name="instance"/>, which is not the broker the relay is connected to.</summary>
	public static WatchEnd ReachedAnother(string instance) => new(instance);
}
