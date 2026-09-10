namespace RoseMcp.UnitTests;

/// <summary>
/// The two names the stub generator and its reader have to agree on.
/// <para>
/// They are spelled twice on purpose. The generator is loaded as an analyzer assembly, and sharing a
/// type across that boundary would make the host and the shadow copy agree on an assembly identity,
/// which is the version-matching problem the generator exists to avoid. A <c>const</c> is compiled
/// into whatever reads it, so two spellings cost nothing at run time and everything if they part
/// company: the report is emitted under one name and looked for under another, the reader finds no
/// document, and the workspace reports no XAML stub generation at all rather than a failure.
/// </para>
/// <para>
/// This test project is the only place that can see both, which is what makes it the right place for
/// the assertion rather than a comment asking the next person to remember.
/// </para>
/// </summary>
public sealed class XamlStubChannelTests
{
	[Test]
	public void The_generator_and_the_reader_agree_on_the_document_name()
	{
		Assert.Equal(XamlStubs.XamlStubReportChannel.HintName, Contracts.XamlStubReportChannel.HintName);
	}

	[Test]
	public void The_generator_and_the_reader_agree_on_the_marker()
	{
		Assert.Equal(XamlStubs.XamlStubReportChannel.Marker, Contracts.XamlStubReportChannel.Marker);
	}
}
