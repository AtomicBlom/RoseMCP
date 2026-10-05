namespace Library;

/// <summary>A label and how many carry it, as a positional record whose Name other types also declare.</summary>
public sealed record Labelled(string Name, int Count);

/// <summary>Reads a positional property, so a rename has a use site to move.</summary>
public static class Labels
{
	public static string Of(Labelled labelled) => labelled.Name;
}
