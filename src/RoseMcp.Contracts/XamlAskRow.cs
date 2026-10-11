namespace RoseMcp.Contracts;

/// <summary>
/// The latest press of the toolbar's Ask button, as the XAML provider answers <c>ask</c>, read by
/// <see cref="XamlWire.ReadAsk"/>.
/// </summary>
/// <param name="Sequence">Counts presses within one provider session, from one.</param>
/// <param name="Kind"><c>element</c>, <c>measurement</c>, <c>colour</c>, or <c>app</c> for a press with nothing picked.</param>
/// <param name="Readable">What was on screen, in words.</param>
/// <param name="Element">The picked element, or a measurement's anchor; null where the press named none.</param>
/// <param name="OtherElement">What a measurement was taken to; null for every other kind.</param>
/// <param name="Colour">The lens's colour as <c>#RRGGBB</c>; null for every other kind.</param>
public sealed record XamlAskRow(
	long Sequence,
	string Kind,
	string Readable,
	ulong? Element,
	ulong? OtherElement,
	string? Colour);
