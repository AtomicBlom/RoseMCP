using Core;

namespace App;

/// <summary>Compiled by App and linked into App.Tests, so the one use written here is a use in each project.</summary>
public static class Shelf
{
	public static string Label() => Labels.Of(new MemoryStore());
}
