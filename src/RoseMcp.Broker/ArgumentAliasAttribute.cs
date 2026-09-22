namespace RoseMcp.Broker;

/// <summary>
/// Another spelling a caller may reach for, accepted for this one parameter and normalised to the
/// parameter's own name before the argument binds.
/// <para>
/// Written on the parameter rather than kept in a table, because an alias is only safe in the
/// company of the other arguments the same tool declares. <c>name</c> means the symbol to describe
/// on six tools and the name to resolve on <c>rose_resolve_name</c>, where it is required and means
/// something else entirely; a table keyed by spelling alone would corrupt the call it was meant to
/// rescue. A declaration cannot reach a tool it is not written on, so the collision is not a rule to
/// enforce but a shape that cannot occur.
/// </para>
/// <para>
/// It also puts the decision where the ambiguity is visible. <c>rose_move_type_to_file</c> takes
/// both <c>filePath</c> and <c>targetPath</c>, so <c>path</c> there is a coin flip and is the one
/// place that alias is left off -- a judgement that reads as a missing attribute beside the others
/// rather than as an exception buried in a list somewhere else.
/// </para>
/// <para>
/// Deliberately absent from the schema. Tool listings are generated from the parameters themselves,
/// so a client is still offered exactly one spelling and goes on being taught it; the alias rescues
/// a call rather than becoming a second supported name.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ArgumentAliasAttribute(string alias) : Attribute
{
	/// <summary>The spelling accepted in place of the parameter's own name.</summary>
	public string Alias { get; } = alias;
}
