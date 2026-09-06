using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The arguments that are an enum wearing a string. Each of these once had a switch whose default
/// was the common case, so a typo did not fail -- it answered a different question and said nothing
/// about it, which is the shape of wrong answer this project treats as the worst available.
/// </summary>
public sealed class ArgumentValueTests
{
	[Theory]
	[InlineData(null, DiagnosticScope.Solution)]
	[InlineData("", DiagnosticScope.Solution)]
	[InlineData("solution", DiagnosticScope.Solution)]
	[InlineData("document", DiagnosticScope.Document)]
	[InlineData("file", DiagnosticScope.Document)]
	[InlineData("PROJECT", DiagnosticScope.Project)]
	public void Reads_the_scopes_it_accepts(string? given, DiagnosticScope expected) =>
		Assert.Equal(expected, ArgumentValues.Scope(given));

	/// <summary>
	/// The one that cost the most: scope "proj" analysed the whole solution and came back with
	/// diagnostics for fourteen projects when one was asked about.
	/// </summary>
	[Fact]
	public void Refuses_a_scope_it_does_not_know_and_says_what_it_takes()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.Scope("proj"));

		Assert.Equal("Unknown scope 'proj'. Use document, project, solution.", error.Message);
	}

	[Theory]
	[InlineData(null, StepDirection.Over)]
	[InlineData("over", StepDirection.Over)]
	[InlineData("in", StepDirection.In)]
	[InlineData("Out", StepDirection.Out)]
	public void Reads_the_step_modes_it_accepts(string? given, StepDirection expected) =>
		Assert.Equal(expected, ArgumentValues.Step(given));

	[Fact]
	public void Refuses_a_step_mode_it_does_not_know()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.Step("into"));

		Assert.Equal("Unknown step mode 'into'. Use in, over, out.", error.Message);
	}

	[Fact]
	public void Reads_a_filter_of_event_kinds()
	{
		var kinds = ArgumentValues.EventKinds("LogMessage, breakpointhit");

		Assert.NotNull(kinds);
		Assert.Equal(2, kinds.Count);
		Assert.Contains(LiveDebugEventKind.LogMessage, kinds);
		Assert.Contains(LiveDebugEventKind.BreakpointHit, kinds);
	}

	/// <summary>Nothing asked for is no filter, which is what an empty string means anyway.</summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Takes_no_filter_as_no_filter(string? given) => Assert.Null(ArgumentValues.EventKinds(given));

	/// <summary>
	/// A misspelt kind was dropped and the rest still applied, so the filter narrowed to the wrong
	/// thing; a filter that lost every name was treated as no filter, which widened the answer to the
	/// hundreds of module loads a freshly started app produces -- burying the one event being waited
	/// for.
	/// </summary>
	[Fact]
	public void Refuses_an_event_kind_it_does_not_know_and_lists_them_all()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.EventKinds("LogMessage,Breakpoint"));

		Assert.Contains("Unknown event kind 'Breakpoint'.", error.Message, StringComparison.Ordinal);
		Assert.Contains("BreakpointHit", error.Message, StringComparison.Ordinal);
		Assert.Contains("ModuleLoaded", error.Message, StringComparison.Ordinal);
	}
}
