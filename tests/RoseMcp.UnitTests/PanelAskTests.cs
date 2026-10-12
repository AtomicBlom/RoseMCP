using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// A press of the in-app panel's Ask button, from the provider's row to the note it opens: the broker opens the
/// Inspector once per press and only for a recent one, the window opens its note once per press and only for one
/// it could have been opened by, and the note is about what the press was about.
/// </summary>
public sealed class PanelAskTests
{
	private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

	private static LiveXamlAsk Press(long sequence, string kind = "element", DateTime? pressed = null) => new()
	{
		Sequence = sequence,
		Kind = kind,
		Readable = "the element Button · Ok picked in the app",
		Element = kind == "colour" ? null : 42UL,
		OtherElement = kind == "measurement" ? 77UL : null,
		Colour = kind == "colour" ? "#C2185B" : null,
		PressedUtc = pressed ?? Now,
	};

	/// <summary>
	/// The provider's row reads into the press the host keeps, dated by the age the provider gave it rather than by
	/// when it was collected: the panel outlives every session, so the two can be hours apart.
	/// </summary>
	[Test]
	public void A_providers_row_reads_into_a_press_dated_by_its_age()
	{
		var row = XamlWire.ReadAsk("ask\t3\tmeasurement\tthe measurement from A to B\t42\t77\t\t7200000\n");

		row.ShouldNotBeNull();
		row.Age.ShouldBe(TimeSpan.FromHours(2));

		var press = LiveXamlAsk.From(row, Now);
		press.Sequence.ShouldBe(3);
		press.Kind.ShouldBe("measurement");
		press.Element.ShouldBe(42UL);
		press.OtherElement.ShouldBe(77UL);
		press.Colour.ShouldBeNull();
		press.PressedUtc.ShouldBe(Now - TimeSpan.FromHours(2));
	}

	/// <summary>
	/// The broker opens the Inspector for a press it has not opened one for, and only one pressed recently: a press
	/// made while nobody was watching is a question nobody is waiting on, however recently it was collected.
	/// </summary>
	[Test]
	public void The_broker_opens_the_inspector_once_per_recent_press()
	{
		var first = Press(1);

		LiveAppSession.ShouldOpen(null, null, Now).ShouldBeFalse();
		LiveAppSession.ShouldOpen(first, null, Now).ShouldBeTrue();
		LiveAppSession.ShouldOpen(first, first, Now).ShouldBeFalse("that press already opened a window");
		LiveAppSession.ShouldOpen(Press(1, pressed: Now - TimeSpan.FromSeconds(2)), first, Now)
			.ShouldBeTrue("a reconnected provider's press 1 is another press");
		LiveAppSession.ShouldOpen(Press(2, pressed: Now - LiveAppSession.AskFreshFor - TimeSpan.FromSeconds(1)), first, Now)
			.ShouldBeFalse();
	}

	/// <summary>
	/// A window opens its note about a press it has not handled, and only one young enough that the press could be
	/// what opened it.
	/// </summary>
	[Test]
	public void A_window_opens_its_note_once_per_press_it_could_have_been_opened_by()
	{
		static LiveAppSessionSummary Summary(LiveXamlAsk? ask, TimeSpan? age) => new()
		{
			SessionId = "a1b2c3d4",
			TargetDescription = "Probe",
			Architecture = TargetArchitecture.X64,
			State = LiveAppSessionState.Ready,
			StartedUtc = Now,
			Uptime = TimeSpan.FromMinutes(1),
			Execution = LiveExecutionState.Running,
			XamlStack = XamlStack.Uwp,
			XamlStackReason = "loaded Windows.UI.Xaml.dll",
			XamlProvider = LiveXamlProvider.Resident,
			XamlAsk = ask,
			XamlAskAge = age,
		};

		var first = Press(1);

		AskAgent.PanelAskToOpen(Summary(null, null), null).ShouldBeNull();
		AskAgent.PanelAskToOpen(Summary(first, TimeSpan.FromSeconds(3)), null)!.Sequence.ShouldBe(1);
		AskAgent.PanelAskToOpen(Summary(first, TimeSpan.FromSeconds(3)), first).ShouldBeNull();
		AskAgent.PanelAskToOpen(Summary(Press(2), AskAgent.PanelAskFreshFor + TimeSpan.FromSeconds(1)), first).ShouldBeNull();
	}

	/// <summary>
	/// The note is about what the press was about: a pick or a measurement names an element, the measurement naming the
	/// other one in its words; a colour names the app's window, belonging to no one element.
	/// </summary>
	[Test]
	[Arguments("element", NoteSubjectKind.Element, "42", false)]
	[Arguments("measurement", NoteSubjectKind.Element, "42", true)]
	[Arguments("colour", NoteSubjectKind.AppWindow, null, false)]
	public void The_note_is_about_what_the_press_was_about(string kind, NoteSubjectKind expected, string? element, bool namesTheOther)
	{
		var subject = AskAgent.FromPanel("a1b2c3d4", Press(5, kind));

		subject.Kind.ShouldBe(expected);
		subject.Session.ShouldBe("a1b2c3d4");
		subject.Element.ShouldBe(element);
		subject.Readable.Contains("element 77", StringComparison.Ordinal).ShouldBe(namesTheOther);
	}
}
