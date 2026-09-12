namespace RoseMcp.Contracts;

/// <summary>
/// A method's source and the places in it a breakpoint can go, for picking a position by reading
/// the code rather than by naming a line.
/// <para>
/// The text and the positions are one answer because they are only meaningful together: a line with
/// no position on it is not somewhere execution stops, and a position with no line beside it is a
/// number. Read from files -- the module's symbols and the source they name -- so it says nothing
/// about the target and costs it nothing.
/// </para>
/// </summary>
public sealed record LiveMethodSource
{
	/// <summary>The method as asked for, in the <c>Assembly!Namespace.Type.Method</c> spelling.</summary>
	public required string Location { get; init; }

	/// <summary>The method as a person says it.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The assembly's simple name.</summary>
	public required string Module { get; init; }

	/// <summary>Whether the module's symbols were read, and if not, why.</summary>
	public required LiveSymbolState Symbols { get; init; }

	/// <summary>
	/// The source file as the compiler recorded it: an absolute path on the machine that built the
	/// module, which need not exist here. Null when there are no symbols to name one.
	/// </summary>
	public string? File { get; init; }

	/// <summary>The line <see cref="Lines"/> starts at, counting from one.</summary>
	public required int FirstLine { get; init; }

	/// <summary>
	/// The method's text, one entry per line. Empty when the file is not on this machine, which is
	/// ordinary for anything out of a package or off a build agent -- <see cref="Detail"/> says so,
	/// and a breakpoint at the method's entry is still available.
	/// </summary>
	public required IReadOnlyList<string> Lines { get; init; }

	/// <summary>
	/// Every place inside the method where execution can stop, in source order. Covers the lambdas,
	/// local functions and state machines written inside it, so a line whose code was compiled
	/// elsewhere is still offered.
	/// </summary>
	public required IReadOnlyList<LiveMethodPosition> Positions { get; init; }

	/// <summary>What the reader needs to know that the text does not say, and null when there is nothing.</summary>
	public string? Detail { get; init; }
}
