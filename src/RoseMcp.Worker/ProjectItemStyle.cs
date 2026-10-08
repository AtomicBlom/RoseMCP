using System.Xml.Linq;

namespace RoseMcp.Worker;

/// <summary>
/// Whether a project compiles the source files in its directory by default, read off the project
/// file rather than evaluated.
/// <para>
/// This decides whether a file that has just appeared is <em>in</em> the build or merely near it. An
/// SDK-style project globs its directory, so a new file beside its siblings is compiled the moment
/// it exists. A legacy project lists every file it compiles, so the same file is not in the build at
/// all until the project file names it -- and reporting diagnostics for it would be inventing a
/// compilation nobody has.
/// </para>
/// <para>
/// A parse and not a build, for the same reason <c>SolutionResolver</c> reads project lists that way:
/// this runs on the read barrier, and paying a design-time build to answer it would cost more than
/// the answer is worth. It follows that a property set in an imported file rather than in the project
/// is not seen -- so the answer is only used to <em>decline</em> to guess, never to overrule
/// something known.
/// </para>
/// </summary>
public static class ProjectItemStyle
{
	/// <summary>
	/// True when the project's own text says nothing to stop the SDK's default globs.
	/// <para>
	/// The benefit of the doubt goes to globbing, because that is what the overwhelming majority of
	/// projects do and because the alternative failure is worse: declining to add a file that really
	/// is compiled leaves every answer about it wrong in a way that looks exactly like an answer
	/// about code with no problems.
	/// </para>
	/// </summary>
	public static bool GlobsSourceFiles(string projectFileText)
	{
		if (string.IsNullOrWhiteSpace(projectFileText)) return true;

		if (Disables(projectFileText, "EnableDefaultCompileItems")) return false;
		if (Disables(projectFileText, "EnableDefaultItems")) return false;

		try
		{
			var root = XDocument.Parse(projectFileText).Root;
			if (root is null) return true;

			// Three spellings of the same thing: the attribute on Project, an Sdk element, and an
			// Import carrying an Sdk attribute. Any of them brings the default globs with it.
			if (root.Attribute("Sdk") is not null) return true;

			return root.Descendants().Any(element =>
				element.Name.LocalName == "Sdk" || element.Attribute("Sdk") is not null);
		}
		catch (System.Xml.XmlException)
		{
			// A project file that will not parse is not a project file this can have an opinion on.
			return true;
		}
	}

	/// <summary>
	/// Whether the project's own text names <paramref name="path"/> in a <c>Compile</c> item and does not
	/// remove it again, which puts the file in the build of a project that lists its files whether it is
	/// on disk yet or not.
	/// <para>
	/// Only a literal path counts. A wildcard or a property is something only evaluation can answer, and
	/// globbing is <see cref="GlobsSourceFiles"/>'s question; this is the other half, for the projects
	/// that answer no to it. A name it cannot read is a name it does not count, so where it is wrong it
	/// reports as outside the build a file that is in it, which is the cautious way round.
	/// </para>
	/// </summary>
	/// <param name="projectFileText">The project file's text.</param>
	/// <param name="projectDirectory">The directory the project file is in, which its paths are relative to.</param>
	/// <param name="path">The source file, as a full path.</param>
	public static bool Lists(string projectFileText, string projectDirectory, string path)
	{
		if (string.IsNullOrWhiteSpace(projectFileText)) return false;

		try
		{
			var root = XDocument.Parse(projectFileText).Root;
			if (root is null) return false;

			var target = Path.GetFullPath(path);
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

			bool Names(string? items) =>
				items is not null
				&& items
					.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Where(item => !item.Contains('*', StringComparison.Ordinal) && !item.Contains("$(", StringComparison.Ordinal))
					.Select(item => Path.GetFullPath(Path.Combine(projectDirectory, item.Replace('\\', Path.DirectorySeparatorChar))))
					.Any(item => string.Equals(item, target, comparison));

			var compiles = root.Descendants().Where(element => element.Name.LocalName == "Compile").ToArray();

			return compiles.Any(element => Names(element.Attribute("Include")?.Value))
				&& !compiles.Any(element => Names(element.Attribute("Remove")?.Value));
		}
		catch (System.Xml.XmlException)
		{
			return false;
		}
	}

	/// <summary>
	/// Whether a property is set to false anywhere in the text. Matched textually rather than by
	/// element, so a condition on it is not read as a value -- which is the safe way round: a
	/// property that might be turning the globs off is treated as though it does, and the only
	/// consequence is that a new file is reported rather than added.
	/// </summary>
	private static bool Disables(string text, string property) =>
		text.Contains($"<{property}>false<", StringComparison.OrdinalIgnoreCase);
}
