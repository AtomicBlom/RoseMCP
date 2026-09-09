using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The arguments that are an enum wearing a string. Each of these once had a switch whose default
/// was the common case, so a typo did not fail -- it answered a different question and said nothing
/// about it, which is the shape of wrong answer this project treats as the worst available.
/// </summary>
public sealed class ArgumentValueTests
{
	[Test]
	[Arguments(null, DiagnosticScope.Solution)]
	[Arguments("", DiagnosticScope.Solution)]
	[Arguments("solution", DiagnosticScope.Solution)]
	[Arguments("document", DiagnosticScope.Document)]
	[Arguments("file", DiagnosticScope.Document)]
	[Arguments("PROJECT", DiagnosticScope.Project)]
	public void Reads_the_scopes_it_accepts(string? given, DiagnosticScope expected) =>
		Assert.Equal(expected, ArgumentValues.Scope(given));

	/// <summary>
	/// The one that cost the most: scope "proj" analysed the whole solution and came back with
	/// diagnostics for fourteen projects when one was asked about.
	/// </summary>
	[Test]
	public void Refuses_a_scope_it_does_not_know_and_says_what_it_takes()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.Scope("proj"));

		Assert.Equal("Unknown scope 'proj'. Use document, project, solution.", error.Message);
	}

	[Test]
	[Arguments(null, StepDirection.Over)]
	[Arguments("over", StepDirection.Over)]
	[Arguments("in", StepDirection.In)]
	[Arguments("Out", StepDirection.Out)]
	public void Reads_the_step_modes_it_accepts(string? given, StepDirection expected) =>
		Assert.Equal(expected, ArgumentValues.Step(given));

	[Test]
	public void Refuses_a_step_mode_it_does_not_know()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.Step("into"));

		Assert.Equal("Unknown step mode 'into'. Use in, over, out.", error.Message);
	}

	[Test]
	public void Reads_a_filter_of_event_kinds()
	{
		var kinds = ArgumentValues.EventKinds(["LogMessage", "breakpointhit"]);

		Assert.NotNull(kinds);
		Assert.Equal(2, kinds.Count);
		Assert.Contains(LiveDebugEventKind.LogMessage, kinds);
		Assert.Contains(LiveDebugEventKind.BreakpointHit, kinds);
	}

	/// <summary>Nothing asked for is no filter, which is what an empty list means anyway.</summary>
	[Test]
	public void Takes_no_filter_as_no_filter()
	{
		Assert.Null(ArgumentValues.EventKinds(null));
		Assert.Null(ArgumentValues.EventKinds([]));
		Assert.Null(ArgumentValues.EventKinds(["   "]));
	}

	/// <summary>
	/// One entry carrying commas is still split, so the spelling this argument had before it became a
	/// list goes on working. It is a list because the rest of the surface passes one for anything
	/// there can be several of, and one CSV among six arrays is a thing a caller has to remember
	/// rather than read.
	/// </summary>
	[Test]
	public void Still_reads_a_comma_separated_entry()
	{
		var kinds = ArgumentValues.EventKinds(["LogMessage,BreakpointHit"]);

		Assert.NotNull(kinds);
		Assert.Equal(2, kinds.Count);
	}

	/// <summary>
	/// A misspelt kind was dropped and the rest still applied, so the filter narrowed to the wrong
	/// thing; a filter that lost every name was treated as no filter, which widened the answer to the
	/// hundreds of module loads a freshly started app produces -- burying the one event being waited
	/// for.
	/// </summary>
	[Test]
	public void Refuses_an_event_kind_it_does_not_know_and_lists_them_all()
	{
		var error = Assert.Throws<ArgumentException>(() => ArgumentValues.EventKinds(["LogMessage,Breakpoint"]));

		Assert.Contains("Unknown event kind 'Breakpoint'.", error.Message, StringComparison.Ordinal);
		Assert.Contains("BreakpointHit", error.Message, StringComparison.Ordinal);
		Assert.Contains("ModuleLoaded", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Which argument is given is what says the scope. One argument called target that meant a file
	/// under one scope and a project under another was the only place in the surface where a name did
	/// not say what it addressed, and the routing layer carries a paragraph about what that cost it.
	/// </summary>
	[Test]
	public void Reads_the_scope_from_what_the_call_named()
	{
		Assert.Equal(DiagnosticScope.Document, DiagnosticTarget.From("Widget.cs", null, null).Scope);
		Assert.Equal("Widget.cs", DiagnosticTarget.From("Widget.cs", null, null).Target);

		Assert.Equal(DiagnosticScope.Project, DiagnosticTarget.From(null, "Core", null).Scope);
		Assert.Equal("Core", DiagnosticTarget.From(null, "Core", null).Target);

		// Nothing named is the whole solution, which is the one case scope is still for.
		Assert.Equal(DiagnosticScope.Solution, DiagnosticTarget.From(null, null, null).Scope);
		Assert.Null(DiagnosticTarget.From(null, null, "solution").Target);
	}

	/// <summary>
	/// A file and a project are different questions, and a scope with nothing to apply it to used to
	/// analyse the whole solution -- an answer many times the size of the one asked, and one that reads
	/// exactly like an answer to it.
	/// </summary>
	[Test]
	[Arguments("Widget.cs", "Core", null, "not both")]
	[Arguments(null, null, "document", "nothing says which one")]
	[Arguments(null, null, "project", "nothing says which one")]
	public void Refuses_a_call_that_does_not_say_what_to_analyse(
		string? filePath,
		string? project,
		string? scope,
		string expected)
	{
		var error = Assert.Throws<ArgumentException>(() => DiagnosticTarget.From(filePath, project, scope));

		Assert.Contains(expected, error.Message, StringComparison.Ordinal);
	}
}
