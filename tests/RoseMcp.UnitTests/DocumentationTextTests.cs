namespace RoseMcp.UnitTests;

/// <summary>
/// A summary read as a line of prose keeps the names its references point at. Concatenating the text
/// nodes drops every <c>&lt;see cref/&gt;</c> and <c>&lt;paramref/&gt;</c>, because each carries its
/// target in an attribute, and the sentence comes back with a hole where its subject was.
/// </summary>
public sealed class DocumentationTextTests
{
	[Test]
	[Arguments("T:RoseMcp.Worker.EditPipeline", "EditPipeline")]
	[Arguments("T:Ns.Outer.Inner", "Inner")]
	[Arguments("T:Ns.Box`1", "Box")]
	[Arguments("T:Ns.Outer`1.Inner`2", "Inner")]
	[Arguments("M:RoseMcp.Worker.EditPipeline.Report", "Report")]
	[Arguments("M:Ns.Type.Write(System.String,System.Int32)", "Write")]
	[Arguments("M:Ns.Type.Map``1(System.Func{``0})", "Map")]
	[Arguments("M:Ns.Type.#ctor(System.String)", "Type")]
	[Arguments("M:Ns.Box`1.#ctor", "Box")]
	[Arguments("M:Ns.Type.#cctor", "Type")]
	[Arguments("M:Ns.Type.Ns#IFoo#Bar", "Bar")]
	[Arguments("M:Ns.Type.System#Collections#Generic#IEnumerable{System#Int32}#GetEnumerator", "GetEnumerator")]
	[Arguments("M:Ns.Type.op_Implicit(Ns.Type)~System.String", "op_Implicit")]
	[Arguments("P:Ns.Type.Listed", "Listed")]
	[Arguments("P:Ns.Type.Item(System.Int32)", "Item")]
	[Arguments("F:Ns.Type._count", "_count")]
	[Arguments("E:Ns.Type.Changed", "Changed")]
	[Arguments("N:RoseMcp.Worker", "Worker")]
	[Arguments("!:Missing", "Missing")]
	[Arguments("!:Ns.Missing.Thing(int)", "Thing")]
	[Arguments("!:List<T>", "List")]
	[Arguments("Plain", "Plain")]
	public void Names_a_cref_by_its_short_name(string cref, string expected)
	{
		DocumentationText.CrefName(cref).ShouldBe(expected);
	}

	[Test]
	[Arguments(
		"""<member name="T:A"><summary>What a tool has to say goes after <see cref="M:RoseMcp.Worker.EditPipeline.Report"/>, which is the shared part.</summary></member>""",
		"What a tool has to say goes after Report, which is the shared part.")]
	[Arguments(
		"""<member name="P:A.B"><summary>The introduced errors a result carries, cut to <see cref="P:RoseMcp.Worker.EditPipeline.Listed"/>.</summary></member>""",
		"The introduced errors a result carries, cut to Listed.")]
	[Arguments(
		"""<member name="M:A.B(System.String)"><summary>Writes <paramref name="written"/>, or works out what writing it would do.</summary></member>""",
		"Writes written, or works out what writing it would do.")]
	[Arguments(
		"""<member name="T:A`1"><summary>Holds one <typeparamref name="T"/>.</summary></member>""",
		"Holds one T.")]
	[Arguments(
		"""<member name="M:A.B"><summary>Returns <see langword="null"/> when there is none.</summary></member>""",
		"Returns null when there is none.")]
	[Arguments(
		"""<member name="M:A.B"><summary>See <see href="https://example.com/spec"/>, or <see href="https://example.com/spec">the spec</see>.</summary></member>""",
		"See https://example.com/spec, or the spec.")]
	[Arguments(
		"""<member name="M:A.B"><summary>Calls <see cref="M:A.C">the other one</see> first.</summary></member>""",
		"Calls the other one first.")]
	[Arguments(
		"""<member name="M:A.B"><summary>Also <seealso cref="T:Ns.Other`1"/>.</summary></member>""",
		"Also Other.")]
	[Arguments(
		"""<member name="M:A.B"><summary>Takes a <c>string</c> and <b>nothing</b> else.</summary></member>""",
		"Takes a string and nothing else.")]
	[Arguments(
		"""<member name="M:A.B"><summary>Not found: <see cref="!:Nowhere"/>.</summary></member>""",
		"Not found: Nowhere.")]
	public void Renders_each_reference_as_the_name_it_points_at(string xml, string expected)
	{
		DocumentationText.Summary(xml).ShouldBe(expected);
	}

	[Test]
	public void Flattens_paragraphs_and_line_breaks_to_one_line()
	{
		const string Xml = """
			<member name="T:A">
			    <summary>
			    The first sentence.<para>The second, about <see cref="T:Ns.Thing"/>.</para>
			    The third.
			    </summary>
			</member>
			""";

		DocumentationText.Summary(Xml).ShouldBe("The first sentence. The second, about Thing. The third.");
	}

	[Test]
	[Arguments(null)]
	[Arguments("")]
	[Arguments("""<member name="T:A"><remarks>Only remarks.</remarks></member>""")]
	[Arguments("""<member name="T:A"><summary>   </summary></member>""")]
	[Arguments("""<member name="T:A"><summary><see/></summary></member>""")]
	[Arguments("""<member name="T:A"><summary>Unclosed</member>""")]
	public void Has_no_summary_when_there_is_nothing_to_render(string? xml)
	{
		DocumentationText.Summary(xml).ShouldBeNull();
	}
}
