namespace RoseMcp.Symbols;

/// <summary>
/// Which of a target's module files declare a type, for binding a location written without its
/// assembly.
/// <para>
/// Asked of metadata rather than of the file name. An assembly is not obliged to be named for the
/// namespaces it holds -- a repository can name its assemblies with a short prefix and its namespaces
/// with the product's full name -- so any rule reading the module off the type name is a guess, and
/// one that fails on the first call somebody makes. Reading a module's type table is cheap and cached,
/// and it is the read the bind needs anyway.
/// </para>
/// <para>
/// A type declared by more than one module is reported, never chosen between. A breakpoint bound in
/// the wrong one never fires, which is indistinguishable from code that never runs.
/// </para>
/// </summary>
public static class TypeOwners
{
	/// <summary>
	/// The modules among <paramref name="modulePaths"/> that declare <paramref name="typeName"/>, in
	/// the order given. A file that cannot be read declares nothing.
	/// </summary>
	/// <param name="modulePaths">The module files to ask, usually a target's loaded modules.</param>
	/// <param name="typeName">The type as metadata spells it, with a <c>+</c> before each nesting level.</param>
	/// <param name="assembly">When given, only modules of this simple name are asked.</param>
	public static IReadOnlyList<string> Of(IEnumerable<string> modulePaths, string typeName, string? assembly)
	{
		var owners = new List<string>();

		foreach (var modulePath in modulePaths)
		{
			if (!Admits(modulePath, assembly)) continue;
			if (MethodTokens.DeclaresType(modulePath, typeName)) owners.Add(modulePath);
		}

		return owners;
	}

	/// <summary>Whether a module file is one a stated assembly name allows. No name allows every module.</summary>
	public static bool Admits(string modulePath, string? assembly) =>
		assembly is null
		|| string.Equals(Path.GetFileNameWithoutExtension(modulePath), assembly, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The sentence for a type several modules declare: which modules, and the spelling that picks one.
	/// The spelling is written out in full because it is the thing the reader types next.
	/// </summary>
	/// <param name="typeName">The type that more than one module declares.</param>
	/// <param name="owners">The modules declaring it; at least two.</param>
	/// <param name="location">The location as written, without an assembly.</param>
	public static string Ambiguity(string typeName, IReadOnlyList<string> owners, string location)
	{
		var names = string.Join(", ", owners.Select(Path.GetFileName));
		var example = $"{Path.GetFileNameWithoutExtension(owners[0])}!{location}";

		return $"{typeName} is declared in {names}; give the assembly to choose one, as {example}";
	}
}
