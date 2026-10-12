using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The two requests that concern the in-app panel's own buttons rather than the tree: collecting a press of Ask,
/// and switching the change marks. Each is one exchange on the provider's pipe and a reading of its reply.
/// <para>
/// Public, and apart from the diagnostics session that calls them under its request lock, so the pipe tests can
/// drive them against a provider that answers on cue -- which a real provider in a real app will not do.
/// </para>
/// </summary>
public static class XamlPanelRequests
{
	/// <summary>
	/// Asks the provider for the latest press of Ask, telling it which press this side already holds so the chip
	/// that lit on the press goes back to rest once it has been collected.
	/// </summary>
	/// <param name="pipe">The provider's pipe, which the caller holds the request lock over.</param>
	/// <param name="seen">The sequence of the press already held; zero for none.</param>
	/// <param name="bound">How long to wait for the reply.</param>
	/// <returns>
	/// Whether the provider answered, and the press it holds: null with an answer means there has been none.
	/// </returns>
	public static (bool Answered, XamlAskRow? Press) Ask(XamlProviderPipe pipe, long seen, TimeSpan bound)
	{
		ArgumentNullException.ThrowIfNull(pipe);

		var reply = pipe.Request($"ask {Math.Max(0, seen)}", bound);
		return reply is null ? (false, null) : (true, XamlWire.ReadAsk(reply));
	}

	/// <summary>
	/// Switches the app's change marks, the same switch as the panel's Changes button.
	/// </summary>
	/// <returns>Where the switch is afterwards, as the provider says; null when it did not answer, or answered with
	/// something this side does not read.</returns>
	public static bool? ChangeMarks(XamlProviderPipe pipe, bool shown, TimeSpan bound)
	{
		ArgumentNullException.ThrowIfNull(pipe);

		var reply = pipe.Request(shown ? "changes on" : "changes off", bound)?.Trim();
		return reply switch
		{
			"on" => true,
			"off" => false,
			_ => null,
		};
	}

	/// <summary>
	/// Switches the change marks and answers with the panel as it reads afterwards, the switch taken from the
	/// provider's own answer to the switching rather than from the read that follows it.
	/// <para>
	/// The read is the caller's, because reading what is picked needs the tree it resolves the pick against. A read
	/// that did not answer carries no switch and a detail saying why, and both stay true of the answer: the switch is
	/// where the provider said it was, and what is picked is not known.
	/// </para>
	/// </summary>
	/// <param name="pipe">The provider's pipe, which the caller holds the request lock over.</param>
	/// <param name="shown">Whether the marks are wanted on.</param>
	/// <param name="bound">How long to wait for each reply.</param>
	/// <param name="readAfter">Reads the panel afterwards; its <see cref="LiveXamlSelection.ChangeMarks"/> is null when it did not answer.</param>
	public static LiveXamlSelection SwitchChangeMarks(XamlProviderPipe pipe, bool shown, TimeSpan bound, Func<LiveXamlSelection> readAfter)
	{
		ArgumentNullException.ThrowIfNull(pipe);
		ArgumentNullException.ThrowIfNull(readAfter);

		if (ChangeMarks(pipe, shown, bound) is not { } switched)
		{
			return new LiveXamlSelection
			{
				Detail = XamlChannelBounds.Unanswered(shown ? "changes on" : "changes off", "the app's change marks to be switched", bound),
			};
		}

		var said = switched
			? "The app marks the elements live edits changed, each tagged with its change, until the tags fade."
			: "The app does not mark what live edits changed; the XAML view still does.";

		var after = readAfter();
		var read = after.ChangeMarks is not null;

		return after with
		{
			ChangeMarks = switched,
			Detail = read ? said : $"{said} What is picked could not be read afterwards: {after.Detail}",
		};
	}
}
