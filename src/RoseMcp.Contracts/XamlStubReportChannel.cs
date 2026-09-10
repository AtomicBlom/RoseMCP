namespace RoseMcp.Contracts;

/// <summary>
/// The two names that cross between the XAML stub generator and the worker that reads its report.
/// <para>
/// The generator is loaded as an analyzer assembly, so the host and the shadow copy must never have
/// to agree on an assembly identity -- that is the version-matching problem the generator exists to
/// avoid. Only these two names cross, and a <c>const</c> is compiled into whatever reads it, so
/// naming them here creates no assembly either side has to load at runtime.
/// </para>
/// <para>
/// Here rather than in the generator because the worker's reference on it is for build ordering
/// alone: the generator has to be built and beside the worker, and must not be loaded twice by
/// being both a project reference and an analyzer file.
/// </para>
/// </summary>
public static class XamlStubReportChannel
{
	/// <summary>The document the report is written to; the worker looks it up by this name.</summary>
	public const string HintName = "__RoseMcpXamlStubReport.g.cs";

	/// <summary>
	/// The line prefix the JSON follows. A comment rather than a constant in the generated file, so
	/// the report adds no symbol to the user's compilation, and a prefix rather than a line number so
	/// a header that grows another line does not move it.
	/// </summary>
	public const string Marker = "// rosemcp-xaml-report:";
}
