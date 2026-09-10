namespace RoseMcp.Contracts;

/// <summary>
/// Whether the XAML diagnostics provider is resident in the target, which decides what the next XAML
/// read costs: a message to a reader that is already there, or an injection first.
/// <para>
/// Distinct from <see cref="LiveXamlTree.Channel"/>, which says how one read was served. This is a
/// fact about the session, and it is the one a status view can act on -- a caller deciding whether to
/// ask for a tree wants to know whether asking is cheap, and a reader watching a session wants to see
/// a provider go.
/// </para>
/// </summary>
public enum LiveXamlProvider
{
	/// <summary>
	/// Nothing has been injected. Not a fault: injection happens on the first XAML request, so this is
	/// every session that has not had one, including targets with no XAML at all.
	/// </summary>
	None,

	/// <summary>A provider is loaded and holding its pipe, so every request is a message on it.</summary>
	Resident,

	/// <summary>
	/// A provider was injected and its pipe is no longer connected, so the next request injects again.
	/// One injection per session is the intent, and the second tap this loads stands the first down --
	/// so the cost is bounded, but a session doing it repeatedly is a channel failing quietly.
	/// </summary>
	Lost,
}
