using System.ComponentModel;

namespace RoseMcp.Contracts;

/// <summary>
/// The request bodies the operator API takes. One file rather than one each, unlike everything else
/// here: they are argument shapes, and each is the same three lines. A result type still gets a file
/// of its own.
/// <para>
/// They are records in Contracts rather than inline parameters so that both ends share them. The
/// inspector serialises exactly what the broker deserialises, and a field added to one is a compile
/// error in the other rather than a silently ignored property.
/// </para>
/// <para>
/// This one and <see cref="AddTracepointRequest"/> are also the items of the MCP tools that set
/// breakpoints and add tracepoints, which take a list of them. So a person in the inspector and an
/// agent describe a breakpoint with the same fields, and the descriptions on them are what the
/// agent's schema shows.
/// </para>
/// </summary>
public sealed record SetBreakpointRequest
{
	/// <summary>Where to stop, as <c>[Assembly!]Namespace.Type.Method</c>.</summary>
	[Description(ToolDescriptions.BreakpointLocationArgument)]
	public required string Location { get; init; }

	/// <summary>
	/// How long a hit is held before the target resumes itself. Null takes the session's default,
	/// which exists so an unattended stop cannot wedge somebody's app.
	/// </summary>
	[Description(ToolDescriptions.AutoContinueSecondsArgument)]
	public int? AutoContinueSeconds { get; init; }

	/// <summary>A cheap value-compare (<c>name OP literal</c>) that gates each hit, if any.</summary>
	[Description(ToolDescriptions.BreakpointConditionArgument)]
	public string? Condition { get; init; }
}

/// <summary>A tracepoint to add: a breakpoint that logs and does not stop.</summary>
public sealed record AddTracepointRequest
{
	/// <summary>Where to log, as <c>[Assembly!]Namespace.Type.Method</c>.</summary>
	[Description(ToolDescriptions.TracepointLocationArgument)]
	public required string Location { get; init; }

	/// <summary>The message each hit logs, with the frame's values interpolated into it.</summary>
	[Description(ToolDescriptions.LogMessageArgument)]
	public string? LogMessage { get; init; }

	/// <summary>Log only every nth hit, for a method called too often to hear every time.</summary>
	[Description(ToolDescriptions.LogEveryNthHitArgument)]
	public int? LogEveryNthHit { get; init; }

	/// <summary>A cheap value-compare (<c>name OP literal</c>) that gates each hit, if any.</summary>
	[Description(ToolDescriptions.TracepointConditionArgument)]
	public string? Condition { get; init; }
}

/// <summary>Which way to step a held target.</summary>
public sealed record StepRequest
{
	/// <summary>
	/// <c>in</c>, <c>over</c> or <c>out</c>. Required, and refused when it is none of those rather
	/// than treated as the common case: a step moves the target, so a typo would move it somewhere
	/// nobody asked and report success.
	/// </summary>
	public required string Mode { get; init; }
}

/// <summary>How long to hold a stop for, or a request to let it go.</summary>
public sealed record HoldRequest
{
	/// <summary>
	/// Seconds to hold, capped by the session. Null takes the default. Ignored when
	/// <see cref="Release"/> is set.
	/// </summary>
	public int? Seconds { get; init; }

	/// <summary>Give the stop back to its safety timer rather than holding it.</summary>
	public bool Release { get; init; }
}

/// <summary>Arming or disarming the in-app overlay that lets a person click an element.</summary>
public sealed record XamlSelectModeRequest
{
	public bool Arm { get; init; } = true;

	/// <summary>Let a click land on anything, rather than only on elements the app's own markup declares.</summary>
	public bool IncludeAllElements { get; init; }

	/// <summary>
	/// Prefer the element the app's markup declares over a control template's internals, so clicking
	/// a button selects the button rather than a TextBlock inside it.
	/// </summary>
	public bool JustMyXaml { get; init; } = true;
}

/// <summary>Which element to select without a click.</summary>
public sealed record XamlSelectElementRequest
{
	/// <summary>A handle, an <c>x:Name</c> as <c>#name</c>, or the address the tree reports.</summary>
	public required string Element { get; init; }
}
