using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The safety net under a signature change: which of the errors a change introduced cannot belong
/// to the caller.
/// <para>
/// Tested here rather than through the tool because it cannot be provoked through the tool. Binding
/// each argument through the compiler is what stops an argument landing on the wrong parameter, so
/// with that in place there is no input that makes one of these errors appear -- which is the whole
/// point of a net, and no reason to leave the rule that decides them unchecked.
/// </para>
/// </summary>
public sealed class CallSiteBindingTests
{
	/// <summary>
	/// The three ids, in a file the change rewrote. Each is the compiler saying an argument list does
	/// not match the parameters it is calling, and those argument lists were written by the tool.
	/// </summary>
	[Theory]
	[InlineData("CS1744")]
	[InlineData("CS1739")]
	[InlineData("CS1501")]
	public void Calls_an_argument_mapping_error_where_it_wrote_its_own(string id)
	{
		var failures = CallSiteBinding.MappingFailures([@"C:\repo\Caller.cs"], [Error(id, @"C:\repo\Caller.cs")]);

		Assert.Equal(id, Assert.Single(failures).Id);
	}

	/// <summary>
	/// An error of any other shape is the caller's work, however it got there. The net is narrow on
	/// purpose: telling someone their own compile error is a tool defect is the same wrong answer in
	/// the other direction.
	/// </summary>
	[Fact]
	public void Leaves_an_error_of_another_shape_to_the_caller()
	{
		var failures = CallSiteBinding.MappingFailures([@"C:\repo\Caller.cs"], [Error("CS0103", @"C:\repo\Caller.cs")]);

		Assert.Empty(failures);
	}

	/// <summary>
	/// A mapping error in a file this did not rewrite a call site in belongs to whoever wrote that
	/// file. Only the argument lists this touched are evidence about this tool.
	/// </summary>
	[Fact]
	public void Leaves_a_mapping_error_in_a_file_it_did_not_rewrite()
	{
		var failures = CallSiteBinding.MappingFailures([@"C:\repo\Caller.cs"], [Error("CS1744", @"C:\repo\Other.cs")]);

		Assert.Empty(failures);
	}

	/// <summary>
	/// A change that rewrote no call site at all can have caused none of these, so nothing it did is
	/// evidence either way.
	/// </summary>
	[Fact]
	public void Claims_nothing_when_it_rewrote_no_call_site()
	{
		Assert.Empty(CallSiteBinding.MappingFailures([], [Error("CS1744", @"C:\repo\Caller.cs")]));
	}

	/// <summary>
	/// Paths are compared the way Windows compares them, since the locations come from Roslyn and the
	/// diagnostics from a second compilation, and neither promises the same spelling of a drive.
	/// </summary>
	[Fact]
	public void Matches_a_path_whatever_its_case()
	{
		var failures = CallSiteBinding.MappingFailures([@"C:\repo\Caller.cs"], [Error("CS1744", @"c:\REPO\caller.cs")]);

		Assert.Single(failures);
	}

	private static DiagnosticEntry Error(string id, string filePath) => new()
	{
		Id = id,
		Severity = "Error",
		Message = "An argument does not fit the parameter it was written for.",
		Project = "Library",
		FilePath = filePath,
		Line = 1,
		Column = 1,
	};
}
