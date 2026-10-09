using System.Text.Json;

using Microsoft.CodeAnalysis.CSharp;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Telling a refusal from a framework's exception that escaped a tool, by where each was thrown. Both
/// arrive as <see cref="ArgumentException"/>, and forwarded as they stand a leaked one reads exactly like
/// advice about the arguments.
/// </summary>
public sealed class ToolFailureTests
{
	/// <summary>
	/// The exception that made this urgent, thrown the way it was: one compilation asked about another's
	/// symbol. It is framed as Roslyn's fault, keeps its words, and loses the parameter name.
	/// </summary>
	[Test]
	public void A_leaked_Roslyn_exception_is_framed_as_a_fault_and_keeps_its_words()
	{
		var asking = CSharpCompilation.Create("Asking", [CSharpSyntaxTree.ParseText("class A { }")]);
		var other = CSharpCompilation.Create("Other", [CSharpSyntaxTree.ParseText("class B { }")]);
		var foreign = other.GetTypeByMetadataName("B")!;

		var exception = Thrown(() => asking.IsSymbolAccessibleWithin(foreign, asking.Assembly));

		ToolFailure.LeakedFrom(exception).ShouldNotBeNull().ShouldStartWith("Microsoft.CodeAnalysis", Case.Sensitive);

		var message = ToolFailure.Message(exception, "rose_resolve_name");
		message.ShouldStartWith("`rose_resolve_name` failed inside Roslyn rather than refusing the call.", Case.Sensitive);
		message.ShouldContain("must be a symbol from this compilation", Case.Sensitive);
		message.ShouldContain("not an argument of `rose_resolve_name`", Case.Sensitive);
		message.ShouldNotContain("(Parameter '", Case.Sensitive);
	}

	/// <summary>A collection indexed past its end is the BCL's exception, not a refusal anybody wrote.</summary>
	[Test]
	public void A_leaked_BCL_exception_is_framed_as_a_fault()
	{
		var exception = Thrown(() => _ = new List<int>()[3]);

		ToolFailure.Message(exception, "rose_outline").ShouldStartWith("`rose_outline` failed inside .NET", Case.Sensitive);
	}

	/// <summary>A refusal Rose's own code wrote is forwarded word for word, parameter name and all, for the schema to settle.</summary>
	[Test]
	public void A_refusal_thrown_by_Rose_is_forwarded_verbatim()
	{
		var exception = Thrown(() => throw new ArgumentException("Nothing is called A.", "symbol"));

		ToolFailure.LeakedFrom(exception).ShouldBeNull();
		ToolFailure.Message(exception, "rose_outline").ShouldBe(exception.Message);
	}

	/// <summary>
	/// A throw helper throws on its caller's behalf, so Rose's own guard through one is still Rose's
	/// refusal rather than the BCL's.
	/// </summary>
	[Test]
	public void A_throw_helper_is_charged_to_the_code_that_called_it()
	{
		string? symbol = " ";
		var exception = Thrown(() => ArgumentException.ThrowIfNullOrWhiteSpace(symbol));

		ToolFailure.ThrowingAssembly(exception).ShouldBe(typeof(ToolFailureTests).Assembly.GetName().Name);
		ToolFailure.LeakedFrom(exception).ShouldBeNull();
	}

	/// <summary>
	/// An I/O failure names the path involved, which is usually one the caller sent, and a JSON failure is
	/// the binder's, which the boundary explains from the schema; neither is framed as a fault.
	/// </summary>
	[Test]
	public void An_io_or_json_failure_stands_as_it_is()
	{
		var missing = Path.Combine(Path.GetTempPath(), $"rose-{Guid.NewGuid():N}.cs");

		ToolFailure.LeakedFrom(Thrown(() => File.ReadAllText(missing))).ShouldBeNull();
		ToolFailure.LeakedFrom(Thrown(() => JsonSerializer.Deserialize<int>("\"x\""))).ShouldBeNull();
	}

	/// <summary>An exception that was never thrown has no frame to read, and is taken at its word.</summary>
	[Test]
	public void An_exception_never_thrown_is_taken_at_its_word()
	{
		var exception = new InvalidOperationException("Not thrown.");

		ToolFailure.ThrowingAssembly(exception).ShouldBeNull();
		ToolFailure.Message(exception, "rose_outline").ShouldBe("Not thrown.");
	}

	[Test]
	[Arguments("Microsoft.CodeAnalysis.CSharp", "Roslyn")]
	[Arguments("Microsoft.CodeAnalysis.Workspaces", "Roslyn")]
	[Arguments("Microsoft.Build", "MSBuild")]
	[Arguments("System.Private.CoreLib", ".NET")]
	[Arguments("System.Linq", ".NET")]
	[Arguments("ClrDebug", "ClrDebug")]
	public void Names_a_component_in_words_a_caller_knows(string assembly, string component)
	{
		ToolFailure.Component(assembly).ShouldBe(component);
	}

	/// <summary>A message that does not end a sentence is given one, so the advice after it reads as its own.</summary>
	[Test]
	public void Ends_the_framework_s_sentence_before_its_own()
	{
		ToolFailure.Leaked("rose_x", ".NET", "Sequence contains no elements")
			.ShouldStartWith("`rose_x` failed inside .NET rather than refusing the call. .NET said: Sequence contains no elements. Any", Case.Sensitive);
	}

	private static Exception Thrown(Action action)
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			return exception;
		}

		throw new InvalidOperationException("Expected the action to throw.");
	}
}
