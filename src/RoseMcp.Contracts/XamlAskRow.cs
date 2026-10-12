namespace RoseMcp.Contracts;

/// <summary>
/// The latest press of the toolbar's Ask button, as the XAML provider answers <c>ask</c>, read by
/// <see cref="XamlWire.ReadAsk"/>.
/// </summary>
/// <param name="Sequence">Counts presses in the app's process from one, and keeps counting across sessions.</param>
/// <param name="Kind"><c>element</c>, <c>measurement</c> or <c>colour</c>.</param>
/// <param name="Readable">What was on screen, in words.</param>
/// <param name="Element">The picked element, or a measurement's anchor; null where the press named none.</param>
/// <param name="OtherElement">What a measurement was taken to; null for every other kind.</param>
/// <param name="Colour">The lens's colour as <c>#RRGGBB</c>; null for every other kind.</param>
/// <param name="Age">
/// How long before the reply the press was made, by the app's own clock. The toolbar outlives every session, so a
/// press can be collected long after it was made, and its age is what tells the two apart.
/// </param>
public sealed record XamlAskRow(
	long Sequence,
	string Kind,
	string Readable,
	ulong? Element,
	ulong? OtherElement,
	string? Colour,
	TimeSpan Age);
