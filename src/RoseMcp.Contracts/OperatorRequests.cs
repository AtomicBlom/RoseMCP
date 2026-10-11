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

/// <summary>A person accepting a kind of reason for a solution: for the projects named, or for the subjects of a reason naming none.</summary>
public sealed record AcceptReasonRequest
{
	/// <summary>The solution, as the absolute path a workspace summary names it by.</summary>
	public required string Workspace { get; init; }

	/// <summary>The kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>The projects to accept it for, as its health entry lists them; empty for a reason naming none, which is accepted by its subjects.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>For a reason that names no project, the subjects to accept it for, as health lists them; ignored where projects are named.</summary>
	public IReadOnlyList<string> Subjects { get; init; } = [];

	/// <summary>What agents read about it once accepted.</summary>
	public AgentsRead AgentsRead { get; init; } = AgentsRead.RoseLine;

	/// <summary>The person's own words for agents; required where <see cref="AgentsRead"/> is <see cref="AgentsRead.Note"/>.</summary>
	public string? Note { get; init; }
}

/// <summary>A person no longer accepting a kind of reason for the projects named, or for the subjects of a reason naming none.</summary>
public sealed record StopAcceptingRequest
{
	/// <summary>The solution, as the absolute path a workspace summary names it by.</summary>
	public required string Workspace { get; init; }

	/// <summary>The kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>The projects to stop accepting it for; empty for a reason naming none, which is named by its subjects.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>For a reason that names no project, the subjects to stop accepting it for; ignored where projects are named.</summary>
	public IReadOnlyList<string> Subjects { get; init; } = [];
}

/// <summary>
/// A person confirming an agent's proposal, optionally changing what agents read: an agent proposes, and
/// the person has the last word on what every other agent is told.
/// </summary>
public sealed record ConfirmProposalRequest
{
	/// <summary>What agents read, in place of what the agent proposed; null keeps the proposal's.</summary>
	public AgentsRead? AgentsRead { get; init; }

	/// <summary>The note, in place of the one the agent proposed; null keeps the proposal's.</summary>
	public string? Note { get; init; }
}

/// <summary>
/// A note a person sends an agent session: whom it is for, what it is about and what it says. It rides on that
/// session's next answer, of any tool.
/// </summary>
public sealed record NoteRequest
{
	/// <summary>The client it is for, by the id <c>/operator/clients</c> lists it under.</summary>
	public required string Client { get; init; }

	/// <summary>What it is about.</summary>
	public required NoteSubject Subject { get; init; }

	/// <summary>What the person has to say.</summary>
	public required string Text { get; init; }
}

/// <summary>A person attaching the debugger to a running process.</summary>
public sealed record AttachRequest
{
	/// <summary>The process, by the id <c>/operator/debug/apps</c> lists it under.</summary>
	public required int ProcessId { get; init; }
}

/// <summary>
/// A person launching something under the debugger: a program, by its full path, with its arguments; or a packaged
/// app, by its application user-model id. Exactly one of the two.
/// </summary>
public sealed record LaunchRequest
{
	/// <summary>The program to launch, as a full path: there is no calling session's folder to measure a relative one from.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>The program's command-line arguments, as one string.</summary>
	public string? Arguments { get; init; }

	/// <summary>The packaged app to launch, as <c>PackageFamilyName!AppId</c>.</summary>
	public string? AppUserModelId { get; init; }

	/// <summary>What the packaged app is called, as <c>/operator/debug/packages</c> named it, for the session and the recent list.</summary>
	public string? DisplayName { get; init; }
}

/// <summary>A person sharing their debug session with one agent session.</summary>
public sealed record ShareRequest
{
	/// <summary>The client to share with, by the id <c>/operator/clients</c> lists it under.</summary>
	public required string Client { get; init; }
}
