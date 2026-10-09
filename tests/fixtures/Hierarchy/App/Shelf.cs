using Core;

namespace App;

/// <summary>Compiled by App and linked into App.Tests, so the one use written here is a use in each project.</summary>
public static class Shelf
{
	public static string Label() => Labels.Of(new MemoryStore());
}

/// <summary>Declared in the linked file, so each project compiles a copy of its own under its own assembly name.</summary>
public interface IShelf
{
	string Material { get; }
}

/// <summary>Implements each project's copy of the interface, in each project.</summary>
public sealed class WoodenShelf : IShelf
{
	public string Material => "wood";
}
