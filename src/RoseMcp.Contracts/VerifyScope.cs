namespace RoseMcp.Contracts;

/// <summary>
/// How much of the solution an edit is compiled against to see what it broke.
/// <para>
/// The cheap answer and the honest one differ, and which is which depends on the edit. A body
/// change cannot be seen outside the projects holding the file. A public member being reshaped,
/// added or removed breaks its dependents by construction, and compiling only the file's own
/// projects then reports a clean edit at exactly the moment it is not one.
/// </para>
/// </summary>
public enum VerifyScope
{
	/// <summary>
	/// Chosen from the edit: the file's own projects for a body change or an effectively private
	/// member, their dependents otherwise. The right answer nearly always, and the only one that is
	/// right without the caller knowing what a member's accessibility implies.
	/// </summary>
	Auto,

	/// <summary>Only the projects holding the edited file.</summary>
	File,

	/// <summary>Those projects and everything that transitively references them.</summary>
	Dependents,

	/// <summary>Every project in the solution.</summary>
	Solution,
}
