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

	private static LiveXamlAsk Press(long sequence, string kind = "element", DateTime? collected = null) => new()
	{
		Sequence = sequence,
		Kind = kind,
		Readable = "the element Button · Ok picked in the app",
		Element = kind == "colour" ? null : 42UL,
		OtherElement = kind == "measurement" ? 77UL : null,
		Colour = kind == "colour" ? "#C2185B" : null,
		CollectedUtc = collected ?? Now,
	};

	/// <summary>The provider's row reads into the press the host keeps, with when it was collected.</summary>
	[Test]
	public void A_providers_row_reads_into_a_press()
	{
		var row = XamlWire.ReadAsk("ask\t3\tmeasurement\tthe measurement from A to B\t42\t77\t\n");

		row.ShouldNotBeNull();
		var press = LiveXamlAsk.From(row, Now);
		press.Sequence.ShouldBe(3);
		press.Kind.ShouldBe("measurement");
		press.Element.ShouldBe(42UL);
		press.OtherElement.ShouldBe(77UL);
		press.Colour.ShouldBeNull();
		press.CollectedUtc.ShouldBe(Now);
	}

	/// <summary>
	/// The broker opens the Inspector for a press it has not opened one for, and only a recent one: a press made before
	/// the broker was watching is a question nobody is waiting on.
	/// </summary>
	[Test]
	public void The_broker_opens_the_inspector_once_per_recent_press()
	{
		LiveAppSession.ShouldOpen(null, 0, Now).ShouldBeFalse();
		LiveAppSession.ShouldOpen(Press(1), 0, Now).ShouldBeTrue();
		LiveAppSession.ShouldOpen(Press(1), 1, Now).ShouldBeFalse("that press already opened a window");
		LiveAppSession.ShouldOpen(Press(1), 4, Now).ShouldBeTrue("a provider that started counting again pressed anew");
		LiveAppSession.ShouldOpen(Press(2, collected: Now - LiveAppSession.AskFreshFor - TimeSpan.FromSeconds(1)), 1, Now)
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

		AskAgent.PanelAskToOpen(Summary(null, null), 0).ShouldBeNull();
		AskAgent.PanelAskToOpen(Summary(Press(1), TimeSpan.FromSeconds(3)), 0)!.Sequence.ShouldBe(1);
		AskAgent.PanelAskToOpen(Summary(Press(1), TimeSpan.FromSeconds(3)), 1).ShouldBeNull();
		AskAgent.PanelAskToOpen(Summary(Press(2), AskAgent.PanelAskFreshFor + TimeSpan.FromSeconds(1)), 1).ShouldBeNull();
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
