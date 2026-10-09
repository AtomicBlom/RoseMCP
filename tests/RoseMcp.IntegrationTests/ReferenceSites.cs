using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>One listed reference with the file it is in, for assertions that ask about both at once.</summary>
public sealed record ListedReference(ReferenceFile File, ReferenceSite Site);

/// <summary>Reads a find-references answer back as the flat list its files group.</summary>
public static class ReferenceSites
{
	/// <summary>Every listed reference, each beside its file, in the order the answer gives them.</summary>
	public static IReadOnlyList<ListedReference> Listed(this ReferencesResult result) =>
		[.. result.Files.SelectMany(file => file.References.Select(site => new ListedReference(file, site)))];
}
