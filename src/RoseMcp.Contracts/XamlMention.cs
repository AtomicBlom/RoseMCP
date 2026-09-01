namespace RoseMcp.Contracts;

/// <summary>
/// A place in markup that names a symbol being renamed.
/// <para>
/// Found by pattern rather than resolved, and deliberately reported rather than changed. A binding
/// path is resolved at runtime against a DataContext that only the running application knows, so
/// nothing here can prove the mention refers to the symbol in question -- but a rename that breaks
/// forty bindings and reports nothing is the worst of the available outcomes, because the compiler
/// will not catch it either.
/// </para>
/// </summary>
public sealed record XamlMention
{
	public required string FilePath { get; init; }

	public required int Line { get; init; }

	/// <summary>What kind of mention: an element, a binding path, an x:Name, or an attribute value.</summary>
	public required string Kind { get; init; }

	/// <summary>The line itself, so the caller can judge it without opening the file.</summary>
	public required string Text { get; init; }
}
