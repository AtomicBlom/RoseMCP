using System.ComponentModel;

namespace RoseMcp.Contracts;

/// <summary>
/// One rule of a structural rewrite, as a caller writes it: what to find, and what to write instead.
/// <para>
/// Declared here rather than beside either host because it is the one argument shape both hosts
/// take whole: the broker forwards it as it arrived, and the worker binds it. One declaration is what
/// keeps the two schemas the same.
/// </para>
/// </summary>
public sealed record PatternRule
{
	/// <summary>What to find.</summary>
	[Description(ToolDescriptions.PatternFindArgument)]
	public required string Find { get; init; }

	/// <summary>What a match becomes.</summary>
	[Description(ToolDescriptions.PatternReplaceArgument)]
	public required string Replace { get; init; }
}
